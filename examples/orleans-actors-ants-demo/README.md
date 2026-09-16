# Orleans actors from ClojureCLR: the ant colony

The classic Clojure ants simulation, with every ant and the world as
[Microsoft Orleans](https://learn.microsoft.com/dotnet/orleans/) actors, written
in ClojureCLR with a small GenServer-style DSL (`orleans.actors`).

```
dotnet run                                   # 40 ants, 400 ticks, drawn in the terminal
dotnet run -- --ants 80 --ticks 1000 --size 60
dotnet run -- --no-render --ticks 200        # just the numbers
dotnet test                                  # rules as pure functions, the actors in-process, the colony on a real silo
```

Ants (`@`) leave the nest (`#`), forage for food (`.` `o` `O`), carry it home and
leave pheromone (`.` `:` in magenta) that evaporates over time. Other ants follow
the trails.

## The DSL

`src/orleans/actors.cljr` is the actor library. An actor type is defined with
`defactor`; the body reads like a GenServer:

```clojure
(defactor ant
  "An ant, addressed as (ant id)."
  {:state :ants.model/ant-state}                        ; spec of the state, optional

  (init [ctx id]                                        ; state on activation
    (let [{:ant/keys [x y dir]} (t/await (actors/call ctx (world "main") :world/spawn id))]
      #:ant{:id id :x x :y y :dir dir :food? false :steps 0}))

  (call :ant/state :ants.model/ant-state [ctx state]    ; request/response, with the reply's spec
    (reply state state))

  (call :ant/step :ants.model/action [ctx {:ant/keys [x y dir] :as state}]
    (let [look (t/await (actors/call ctx (world "main") :world/look (select-keys state [:ant/x :ant/y :ant/dir])))]
      ...
      (reply :move (assoc state :ant/x nx :ant/y ny)))))
```

| OTP | here | Orleans underneath |
|---|---|---|
| `GenServer.init/1` | `(init [ctx id] ...)` returns the state | `OnActivateAsync` |
| `handle_call/3` | `(call :ant/step [ctx state payload] (reply value state))` | grain method `Call`, one message at a time |
| `handle_cast/2` | `(cast :world/clear-trails [ctx state payload] state)` | `[OneWay]` grain method `Cast` |
| `handle_info/2` | `(info :world/evaporate [ctx state payload] state)` with `send-after` / `send-every` | grain timers, never concurrent with handlers |
| `GenServer.call/2` | `(actors/call ctx ref :world/look payload)` returns a Task; `call!` blocks | grain reference proxy |
| `GenServer.cast/2` | `(actors/cast ctx ref :world/clear-trails payload)` | |
| a pid | `(ant 7)`, `(world "main")`: refs are data, `{:actor/type :ant :actor/id "7"}` | virtual actors: exist when addressed, activated on first message, deactivated when idle |
| a supervisor | `{:on-error :restart}` (default), `:resume` or `:stop` on the actor | the runtime re-initialises a crashed actor; the caller still gets the error |
| `GenServer.stop/1` | `(actors/stop ctx)` | `DeactivateOnIdle` |
| persistent state (Mnesia, ETS...) | `{:persist true}`, `(resume [ctx stored] ...)`, `(actors/forget ctx)` | the storage protocol, called by the runtime |
| a typespec / struct | `{:state ::spec}` on the actor; `(s/def :world/move ...)` for a message's payload; `(call :world/move ::moved? ...)` for its reply | state and reply checked after every message, payload on every incoming one |

**The context.** The first argument of every handler is the actor's context,
a map with namespaced keys, and everything an actor does besides computing
its next state goes through it: sending (`call`, `cast`) through
`:actor/system`, timers to itself (`send-after`, `send-every`) and `stop`
through `:actor/host`, its own ref as `:actor/self`, and the system's
`:actor/storage`, so an actor that wants to keep something of its own writes
`(storage/write-state (:actor/storage ctx) ...)`. The context comes from
whatever hosts the actor, so nothing is global: on a silo the system reaches
the cluster through the grain, in a test it is an in-process system. `call`
and `cast` take a running system just as well as a context, so a driver, a
test and an actor use one API. `:actor/system` and `:actor/host` are two
protocols in `orleans.actors`, `Messaging` (call, cast) and `Host` (timers,
stop); a host is an implementation of them plus a state slot.

Handler bodies run in an async context, so they can `t/await` calls to other
actors. Messages are Clojure values. Inside a silo they are passed by
reference (they are immutable, so Orleans has nothing to copy); between silos
they travel as EDN through a small codec in the glue. Both mean the same thing.
Actors of the same type are independent; an actor handles one message at a
time, and a call cycle (A calls B calls A) deadlocks, exactly as with a
GenServer.

**Two hosts, one runtime.** `orleans.actors.silo/start!` runs an Orleans silo
and returns it as a system. `orleans.actors.local/system` is the same actors
without Orleans: they live in a map, messages are handled one after the other
(the Task `call` or `cast` returns completes once the message is handled,
which is before it returns when nothing waits on the outside world), and
timers are fired on demand with `run-timers!`, so a test sends a message and
looks at the state right after:

```clojure
(let [system (local/system)]
  (actors/call! system (world "main") :world/configure #:world{:size 20 :home-size 8})
  (actors/call! system (ant 1) :ant/step)
  (local/state system (world "main")))          ; the grid, with the ant on it
```

Both hosts call the same three runtime functions (`activate`, `handle`,
`recover`), so specs, persistence and the error policy behave the same;
what a test proves on the local system holds on the silo. Two things differ
because they are Orleans: an actor calling itself works locally and
deadlocks on a silo, and nothing crosses the wire locally.
`test/ants/actors_test.cljr` tests the actors on the local system, 130
generated cases included, in about a second; `test/ants/colony_test.cljr`
runs them on a real silo. A local tick of 30 ants takes about 3 ms against
12 ms on a silo: the difference is Orleans doing its job.

**State, messages and replies as specs.** `src/ants/model.cljr` describes the
colony with clojure.spec, using namespaced keys (`:ant/x`, `:cell/food`,
`:world/size`, `:move/from`) so a map says what it is wherever it turns up.
A message type is a namespaced keyword too, and the spec registered under it
is its payload: `(s/def :world/look (s/keys :req [:ant/x :ant/y :ant/dir]))`
says both that `:world/look` is a message the world handles and what it
carries, the way a key is declared. A call clause can name a spec for its
reply, `(call :world/look ::look-view [ctx state pos] ...)`; a cast or an
info message has no reply, its effect is the next state, which the `:state`
spec covers. An actor with a `:state` spec has its state checked after `init`
and after every message, and a reply is checked against its spec; a
violation of either is a bug in the actor, so it fails and its `:on-error`
policy applies (a restart, by default). A payload that does not conform is
rejected before the handler runs: the caller gets an `ex-info` with the
explanation and the actor is untouched. `(actors/describe :world)` shows the
state spec and, per message, the payload and reply specs.
The same specs generate the data for the property-based tests in
`test/ants/logic_test.cljr`. The checks cost what `s/valid?` costs on the
state, which on ClojureCLR is about 15 µs per validated map, so the grid is
described with `s/every-kv` (which samples) rather than `s/map-of`, the
runtime examines 10 sampled elements per check (`:state-check 50` on the
system raises that, `:state-check false` turns state checks off), and a
handler that returns the state it was given is not checked again. With
these specs a tick of 30 ants on a silo costs about 12 ms with the checks
and 10 ms without.

**Every valid message, generated.** `(actors/message-generator :world :call)`
turns an actor's declared messages and payload specs into a test.check
generator, so a property can send an actor everything it claims to accept
and let the state spec judge the result. On the local system each case gets a
fresh world and the whole grid is checked after every message:

```clojure
(defspec the-world-handles-every-valid-message 100
  (prop/for-all [messages (gen/vector (actors/message-generator :world :call) 1 5)]
    (let [system (local/system {:state-check 5000})]
      (doseq [[type payload] messages]
        (actors/call! system (world "main") type payload))
      (s/valid? ::model/look-view (actors/call! system (world "main") :world/look #:ant{:x 0 :y 0 :dir 0})))))
```

Its first run found a real bug: configure a one-cell nest, spawn two ants,
and the world threw "the nest is full".

**Persistence, behind a protocol.** `{:persist true}` on an actor stores its
state after `init` and after every message that changed it, and an actor
that is activated again, after `stop`, an idle deactivation, a crash or a
silo restart, gets it back: `(resume [ctx stored] state)` runs instead of
`init` (by default the stored state is kept as it is). A crash restarts a
persisting actor from its stored state, which is the last one that conformed
to the spec. Where the state goes is `orleans.actors.storage/Storage`, three
functions (`read-state`, `write-state`, `clear-state`), given to the system
at start and reached by an actor as `:actor/storage`. Three implementations
come with the example:

- `orleans.actors.storage.edn-files`: one EDN file per actor, what would be on
  the wire, under `.actors/` in the working directory by default; ids are
  encoded into file names, so any id stays inside the directory. Silos of a
  cluster share it.
- `orleans.actors.storage.sqlite`: one table, `actors(type, id, state)`, in a
  SQLite file (`Microsoft.Data.Sqlite`). Postgres or FoundationDB are the
  same three functions against a server.
- `orleans.actors.storage.memory`: an atom, for tests.

```clojure
(silo/start! {:storage (sqlite/storage "colony.db")})
(local/system {:storage (memory/storage)})
```

**More than one silo.** `(silo/start {:primary-port 11111 :silo-port 11112
:gateway-port 30001})` joins the cluster whose primary silo listens on that
port on this machine; actors are shared by every silo of the cluster, each
living on one silo and reached from all. The test suite starts a second silo
in another process (`test/ants/second_silo.cljr`) and has it talk to the
world actor of the first: every message crosses the wire through the codec,
including a rejection and an unknown message. Across machines the localhost
clustering is replaced by a membership provider, again one line in the glue.
After a silo leaves, the others need a few seconds to see it gone and to take
over its share of the grain directory; messages routed through it fail until
then, which is what `silo/active-silos` is for. An actor whose directory
registration was on the silo that left is activated afresh on its next
message, so an actor that does not persist can lose its state on a cluster
change. That is Orleans, not the DSL: persist what matters.

There is no supervisor tree to define: Orleans is the supervisor. Every actor
is always "running" as far as its callers are concerned; if it crashes it is
re-initialised, and if a silo dies its actors come back on another one on the
next message.

## The glue

Orleans generates proxies and serializers from C# grain interfaces, so a small C#
class library (`glue/`) holds:

- `ActorMessage` and `ActorReply`: a type name and a payload, any Clojure
  value, marked immutable so Orleans passes them by reference inside a silo.
- `ClojureCodec` and `ClojureCopier`: how a Clojure value leaves the process
  (as EDN, through `IClojureWire`, implemented in Clojure) and how it is
  copied (it is not: it is immutable).
- `IActorGrain`: `Call` and `Cast`. Every Clojure actor type is hosted by the
  same grain interface; the grain key is `"type/id"`.
- `ActorGrain`: the one grain class. It owns the state slot and the timers and
  hands every event (`OnActivate`, `OnCall`, `OnCast`, `OnInfo`, `OnError`) to
  an `IActorHost`, which `orleans.actors.silo` implements with `reify`. The
  host and the wire are services the silo is started with; grains and the
  codec get them injected, nothing is static.
- `Actors.StartSiloAsync` and `StopSiloAsync`: an in-process silo with localhost
  clustering. `silo/start` and `silo/shutdown` await them; `start!` and
  `shutdown!` block, for `-main`, tests and the REPL.

Everything else, including the dispatch on actor type, the state handling,
persistence and the error policy, is Clojure.

## Layout

- `src/orleans/actors.cljr` the DSL, the context protocols and the runtime.
- `src/orleans/actors/silo.cljr` the Orleans host; `local.cljr` the in-process
  host for tests; `wire.cljr` values as EDN.
- `src/orleans/actors/storage.cljr` the storage protocol; `storage/edn_files.cljr`,
  `storage/sqlite.cljr`, `storage/memory.cljr` its implementations.
- `src/ants/model.cljr` the colony as specs: actor states, messages and
  replies, what an ant sees.
- `src/ants/logic.cljr` the colony rules as pure functions (ranking, weighted
  random choice, evaporation), no actors involved.
- `src/ants/world.cljr` the world actor: owns the grid, evaporates pheromone on
  a timer it sets for itself.
- `src/ants/ant.cljr` the ant actor: created on first use, asks the world for a
  place in the nest, then look, decide, act on every `:step`.
- `src/ants/render.cljr` terminal drawing, `src/ants/main.cljr` the driver.
- `test/ants/logic_test.cljr` the rules, plus property-based tests generated
  from the specs; `test/ants/actors_test.cljr` the actors on the local system,
  including the storages and every valid message; `test/ants/colony_test.cljr`
  the colony on a real silo (own ports, so it can run next to `dotnet run`),
  persistence on files and a second silo; `test/ants/test_actors.cljr` the
  small actors both use.

## Notes

- `IHost`, `LogLevel` and `Microsoft.Data.Sqlite` live in assemblies that
  ClojureCLR cannot find by name until they are loaded; the namespaces that
  use them load them with `assembly-load` before their `ns` form.
- The simulation is driven tick by tick from `-main` so that it can be drawn;
  each ant could as well run on its own `send-every` timer.
- Orleans complains that the silo does not run with server GC. For a demo that
  is fine; set `<ServerGarbageCollection>true</ServerGarbageCollection>` for a
  real service.
