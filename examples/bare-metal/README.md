# Bare Metal — the .NET features JVM Clojure can't touch

Hardware SIMD, reified generics, zero-allocation loops, P/Invoke into the OS,
and C# in the same project — all from ClojureCLR, all in one `dotnet run`.

Every act here is something that on the JVM is either an incubating API
(Vector API), a still-unshipped project (Valhalla value types / reified
generics), or a separate build-system headache (mixed-language javac+Clojure
modules). On the CLR it's a type hint away.

```
$ dotnet run

════════════════════════════════════════════════════════════════
 BARE METAL — Apple M4 Pro | SIMD 4-wide | hw-accel: true
 (CPU name fetched via raw sysctlbyname P/Invoke, by the way)
════════════════════════════════════════════════════════════════

Act 1 — dot product ladder (10M floats; boxed rung uses 1M)
  idiomatic (boxed seqs, 1M)        1988 ms   1988 ns/elem   408 B/elem  baseline
  hinted loop (Clojure)             697 ms    69.7 ns/elem   48 B/elem   28.5x faster
  SIMD Vector<float> (Clojure!)     13 ms     1.3 ns/elem    0 B/elem    1529.2x faster
  SIMD C# (same project)            62 ms     6.2 ns/elem    0 B/elem    320.6x faster
  results agree: true = 10000000.0

Act 2 — reified generics: List<double>, no erasure, no boxing
  runtime type: System.Collections.Generic.List`1[[System.Double, ...]]
  sum boxed PersistentVector (1M)   59 ms     59 ns/elem     24 B/elem   baseline
  sum List<double> (10M)            40 ms     4 ns/elem      0 B/elem    14.8x faster

Act 3 — LINQ over that list with a Clojure lambda:
  (Enumerable/Count (Enumerable/Where dl big?)) => 10000000

Act 4 — P/Invoke: the OS is one attribute away
  zlib crc32 => 0x5926B191 (libz.dylib, called directly)
  sysctl     => Apple M4 Pro (raw Darwin syscall)
```

Numbers from one laptop run under load — expect variance (the Clojure SIMD rung
is stable; the others swing with thermal throttling). The shape holds: **pure
Clojure SIMD runs at C#-class speed with zero allocation**, ~3 orders of
magnitude over idiomatic boxed code. This is a demo, not a rigorous benchmark —
for real measurements use BenchmarkDotNet.

## The punchlines

**SIMD from pure Clojure** (`Vector<float>` = NEON/AVX, stable API since forever):

```clojure
(alias-type FloatVector |System.Numerics.Vector`1[System.Single]|)

(defn simd-dot ^double [^|System.Single[]| xs ^|System.Single[]| ys]
  (let [n (alength xs) vn (FloatVector/Count)]
    (loop [i (int 0) acc FloatVector/Zero]
      (if (<= (+ i vn) n)
        (recur (+ i vn)
               (FloatVector/op_Addition acc
                 (FloatVector/op_Multiply (FloatVector. xs i) (FloatVector. ys i))))
        (double (Vector/Dot (type-args System.Single) acc FloatVector/One))))))
```

A 16-byte SIMD struct riding through `loop`/`recur` unboxed. `0 B/elem` in the
table is the receipt.

**P/Invoke straight from a Lisp** — `Native.cs` is 15 lines in the same csproj:

```csharp
[DllImport("z", EntryPoint = "crc32")]
public static extern uint Crc32(uint crc, byte[] buf, uint len);
```

```clojure
(Native/Crc32 0 (.GetBytes Encoding/UTF8 "hello") (uint 5)) ;=> 0x3610A686
```

No JNI, no JNA, no Panama downcall handles, no bindings generator. The C# file
and the Clojure file build together in one `dotnet build` — that's the
Clojure.MSBuild part.

**Reified generics** — `List<double>` stores unboxed doubles and knows its type
at runtime; a hinted interop loop over it sums 10M doubles at ~C# speed with
zero garbage. And LINQ accepts a Clojure fn as a `Func<double,bool>` via
`gen-delegate`.

## Compiler survival guide

Learned the hard way, so you don't have to:

1. **Struct locals can't take type hints** — `^FloatVector acc` in a `loop` is
   rejected ("primitive initializer"). Good news: the type flows from the init
   expression, so `(loop [acc FloatVector/Zero] ...)` is already unboxed.
2. **Generic static methods (`Vector/Add`) don't infer from struct args** — call
   the operator on the *constructed* type instead: `FloatVector/op_Addition`.
   For genuinely generic ones pass `(type-args System.Single)` explicitly.
3. **Don't `let`-bind intermediate structs** — the local loses the static type
   and every op after it goes through the DLR. Nest the calls; the reflection
   warnings (keep `*warn-on-reflection*` on!) tell you when you slipped.
4. **`^floats`-style hints don't reach interop overload resolution** — use the
   CLR array hint `^|System.Single[]|` when a ctor/method needs to bind.
5. **`aget` on primitive arrays currently boxes** (~24 B/elem) — for hot loops
   prefer `List<T>` interop (`.get_Item` binds statically, 0 alloc) or SIMD
   chunks. That's why the "hinted loop" rung still allocates.
6. **Loops carrying structs must be in tail position** — a `loop` in expression
   position gets closure-wrapped and structs can't be captured.
