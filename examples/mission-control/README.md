# Mission Control — ClojureCLR async/await on .NET 11

Four Kestrel servers in one process, zero blocked threads, ~300 lines of Clojure.

Three "spacecraft subsystem" microservices (`telemetry`, `navigation`, `life-support`),
an API gateway that aggregates them, and a load test that hammers the gateway with
150 concurrent snapshots — every I/O hop a real .NET async state machine compiled
from `^:async` Clojure functions ([.NET runtime async](https://github.com/dotnet/runtime/issues/109632)).

```
                        ┌──────────────────────────── one process ─┐
   150 concurrent  ───▶ │  gateway :6164/snapshot                  │
   HTTP requests        │     │  Task.WhenAll fan-out              │
                        │     │  + Task.WhenAny timeout race       │
                        │     ├──▶ telemetry    :6161/status       │
                        │     ├──▶ navigation   :6162/status       │
                        │     └──▶ life-support :6163/status       │
                        │              (randomly spikes ~400ms →   │
                        │               gateway degrades, not dies)│
                        │  mission log: System.Threading.Channels  │
                        └──────────────────────────────────────────┘
```

## Run it

```bash
dotnet run            # full demo: snapshot + load test + stats
dotnet run --serve    # just serve; curl http://127.0.0.1:6164/snapshot
dotnet test           # unit tests for the pure stats namespace
```

Ports default to 6161–6164; override with `MC_BASE_PORT=7000 dotnet run`.

## What it demonstrates

| .NET concept | Clojure code |
|---|---|
| `async`/`await` state machines | `(defn ^:async f [] (t/await task))` — [`services.cljr`](src/mission/services.cljr) |
| `Task.WhenAll` fan-out | gateway snapshot — [`gateway.cljr`](src/mission/gateway.cljr) |
| `Task.WhenAny` timeout + fallback | `fetch-with-timeout` — a per-dependency budget in 8 lines |
| `System.Threading.Channels` | mission-log producer/consumer — [`core.cljr`](src/mission/core.cljr) |
| ASP.NET Core minimal APIs (Kestrel) | [`web.cljr`](src/mission/web.cljr) — ~35 line wrapper |
| `HttpClient` async I/O | `fetch-status`, the load test |
| NuGet interop | [Spectre.Console](https://spectreconsole.net/) tables + bar charts |
| `dotnet test` | [`stats_test.cljr`](test/mission/stats_test.cljr) via Clojure.MSBuild.TestAdapter |

The interesting part: the load test fires 150 gateway requests at once and each
snapshot fans out to 3 services, so ~600 HTTP calls are in flight on a handful of
thread-pool threads. Sequential vs concurrent throughput is measured and printed —
expect an order-of-magnitude gap.

## AOT compilation

Every namespace in this example — Kestrel wrappers, async state machines,
Spectre rendering — AOT-compiles to .NET DLLs:

```bash
dotnet build -p:ClojureCompileOnBuild=true \
  "-p:ClojureNamespacesToCompile=mission.stats%3Bmission.web%3Bmission.services%3Bmission.gateway%3Bmission.core"
```

That drops `mission.*.cljr.dll` files into the output directory; on the next run
the compiled assemblies are loaded instead of compiling `.cljr` sources at
startup. (The `%3B` is an MSBuild-escaped `;` — inside a `.csproj` you write
plain semicolons.)

## Async cheat sheet (ClojureCLR 1.12.3-alpha8+)

```clojure
(require '[clojure.clr.async.task.alpha :as t])

(defn ^:async fetch [url]                 ; compiles to a .NET async state machine
  (t/await (.GetStringAsync client url))) ; suspends, doesn't block

(t/async (t/await (t/delay-task 100)) :done) ; inline async block → Task<Object>
(t/result task)                              ; block + unwrap (outside async code)
(t/wait-all tasks)                           ; Task.WaitAll
```

Things the compiler needs from you:

1. **`t/await` needs a statically-typed Task expression.** Interop calls whose
   return type the compiler can infer (`(.GetStringAsync ^HttpClient c url)`) work
   directly. Calling another `^:async` **fn** returns `Object` as far as the
   compiler knows — bind it with a hint first:

   ```clojure
   (alias-type TaskObj |System.Threading.Tasks.Task`1[System.Object]|)
   (let [^TaskObj task (other-async-fn)] (t/await task))
   ```

2. **`ValueTask` awaits: call `.AsTask`.** Channel reads return `ValueTask<T>`;
   awaiting them directly miscompiles in alpha8. `(t/await (.AsTask (.ReadAsync reader ct)))`.

3. **Pass optional parameters explicitly.** `(.ReadAsync reader)` and
   `(.Complete writer)` fail to bind — write `(.ReadAsync reader CancellationToken/None)`
   and `(.Complete writer nil)`.

4. `await` works inside `loop`/`recur`, `try`/`catch`, and with destructured
   params — see `run-sequential` and `consume-log` in this example.
