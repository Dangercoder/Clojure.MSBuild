# Orleans actors from ClojureCLR: the ant colony

The classic Clojure ants simulation, with every ant and the world as
[Microsoft Orleans](https://learn.microsoft.com/dotnet/orleans/) actors, written
in ClojureCLR with a small GenServer-style DSL (`orleans.actors`).

```
dotnet run                                   # 40 ants, 400 ticks, drawn in the terminal
dotnet run -- --ants 80 --ticks 1000 --size 60
dotnet run -- --no-render --ticks 200        # just the numbers
dotnet test                                  # rules as pure functions + the colony on a real silo
```

Ants (`@`) leave the nest (`#`), forage for food (`.` `o` `O`), carry it home and
leave pheromone (`.` `:` in magenta) that evaporates over time. Other ants follow
the trails.

## The DSL

`src/orleans/actors.cljr` is the whole actor library (about 250 lines). An actor
type is defined with `defactor`; the body reads like a GenServer:

```clojure
(defactor ant
  "An ant, addressed as (ant id)."
  {:state :ants.model/ant-state}                        ; spec of the state, optional

  (init [self id]                                       ; state on activation
    (let [{:ant/keys [x y dir]} (t/await (actors/call (world "main") :spawn id))]
      #:ant{:id id :x x :y y :dir dir :food? false :steps 0}))

  (call :state [self state]                             ; request/response
    (reply state state))

  (call :step [self {:ant/keys [x y dir] :as state}]
    (let [look (t/await (actors/call (world "main") :look (select-keys state [:ant/x :ant/y :ant/dir])))]
      ...
      (reply :moved (assoc state :ant/x nx :ant/y ny)))))
```

| OTP | here | Orleans underneath |
|---|---|---|
| `GenServer.init/1` | `(init [self id] ...)` returns the state | `OnActivateAsync` |
| `handle_call/3` | `(call :msg [self state payload] (reply value state))` | grain method `Call`, one message at a time |
| `handle_cast/2` | `(cast :msg [self state payload] state)` | `[OneWay]` grain method `Cast` |
| `handle_info/2` | `(info :msg [self state payload] state)` with `send-after` / `send-every` | grain timers, never concurrent with handlers |
| `GenServer.call/2` | `(actors/call ref :msg payload)` returns a Task; `call!` blocks | grain reference proxy |
| `GenServer.cast/2` | `(actors/cast ref :msg payload)` | |
| a pid | `(ant 7)`, `(world "main")`: refs are just `type/id` keys | virtual actors: exist when addressed, activated on first message, deactivated when idle |
| a supervisor | `{:on-error :restart}` (default), `:resume` or `:stop` on the actor | the runtime re-initialises a crashed actor; the caller still gets the error |
| `GenServer.stop/1` | `(actors/stop self)` | `DeactivateOnIdle` |
| a typespec / struct | `{:state ::spec}` on the actor, `(call :move ::move [self state payload] ...)` on a handler | checked after every message, and on every incoming payload |

Handler bodies run in an async context, so they can `t/await` calls to other
actors. Messages are Clojure data: they travel as EDN, so maps, vectors,
keywords, numbers, strings and sets all work, across silos too. Actors of the
same type are independent; an actor handles one message at a time, and a call
cycle (A calls B calls A) deadlocks, exactly as with a GenServer.

**State and messages as specs.** `src/ants/model.cljr` describes the colony
with clojure.spec, using namespaced keys (`:ant/x`, `:cell/food`, `:world/size`,
`:move/from`) so a map says what it is wherever it turns up. An actor with a
`:state` spec has its state checked after `init` and after every message; a
violation is a bug in the actor, so it fails and its `:on-error` policy
applies (a restart, by default). A handler with a payload spec rejects
payloads that do not conform before it runs: the caller gets an `ex-info`
with the explanation and the actor is untouched. `(actors/describe :world)`
shows the state spec, the messages and their payload specs.
The same specs generate the data for the property-based tests in
`test/ants/logic_test.cljr`. The checks cost what `s/valid?` costs on the
state, which on ClojureCLR is about 15 µs per validated map, so the grid is
described with `s/every-kv` (which samples) rather than `s/map-of`, the
runtime examines 10 sampled elements per check (`(actors/check-state! 50)`
raises that, `(actors/check-state! false)` turns state checks off), and a
handler that returns the state it was given is not checked again. With
these specs a tick of 30 ants costs about 27 ms with the checks and 23 ms
without.

There is no supervisor tree to define: Orleans is the supervisor. Every actor
is always "running" as far as its callers are concerned; if it crashes it is
re-initialised, and if a silo dies its actors come back on another one on the
next message.

## The glue

Orleans generates proxies and serializers from C# grain interfaces, so a small C#
class library (`glue/`) holds:

- `ActorMessage`: a type name and an EDN payload.
- `IActorGrain`: `Call` and `Cast`. Every Clojure actor type is hosted by the
  same grain interface; the grain key is `"type/id"`.
- `ActorGrain`: the one grain class. It owns the state slot and the timers and
  hands every event (`OnActivate`, `OnCall`, `OnCast`, `OnInfo`, `OnError`) to
  an `IActorHost`, which `orleans.actors` implements with `reify`.
- `Actors.StartSilo`: an in-process silo with localhost clustering.

Everything else, including the dispatch on actor type, the state handling and
the error policy, is Clojure.

## Layout

- `src/orleans/actors.cljr` the DSL and runtime.
- `src/ants/model.cljr` the colony as specs: actor states, messages, what an
  ant sees.
- `src/ants/logic.cljr` the colony rules as pure functions (ranking, weighted
  random choice, evaporation), no actors involved.
- `src/ants/world.cljr` the world actor: owns the grid, evaporates pheromone on
  a timer it sets for itself.
- `src/ants/ant.cljr` the ant actor: created on first use, asks the world for a
  place in the nest, then look, decide, act on every `:step`.
- `src/ants/render.cljr` terminal drawing, `src/ants/main.cljr` the driver.
- `test/ants/logic_test.cljr` the rules, plus property-based tests generated
  from the specs; `test/ants/colony_test.cljr` runs the actors on a real silo
  (own ports, so it can run next to `dotnet run`), including rejected
  payloads and a restart after a state violation.

## Notes

- `IHost` and `LogLevel` live in shared-framework assemblies that ClojureCLR
  cannot find by name until they are loaded; `orleans.actors` loads them with
  `assembly-load` before its `ns` form.
- The simulation is driven tick by tick from `-main` so that it can be drawn;
  each ant could as well run on its own `send-every` timer.
- Orleans complains that the silo does not run with server GC. For a demo that
  is fine; set `<ServerGarbageCollection>true</ServerGarbageCollection>` for a
  real service.
