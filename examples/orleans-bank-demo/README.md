# A bank that survives `kill -9`: Orleans grain storage on PostgreSQL, from ClojureCLR

Accounts and transfers as Clojure actors on a cluster of
[Microsoft Orleans](https://learn.microsoft.com/dotnet/orleans/) silos. Their
state is persisted by Orleans' own grain storage in PostgreSQL, and a chaos
monkey kills a silo process every few seconds while thousands of transfers
are in flight. At the end an audit reads the books back from the database:
every transfer happened exactly once or not at all, and not one öre
appeared or vanished.

```
docker compose up -d        # PostgreSQL 17 with Orleans' tables (sql/)
dotnet run                  # 3 silos, 20 accounts, 30 s of transfers, a kill -9 every 6 s
dotnet run -- chaos --silos 4 --accounts 50 --seconds 60 --kill-every 4 --workers 32
dotnet run -- audit <run>   # audit a past run in a fresh process, from nothing but the database
dotnet test                 # the saga, a deterministic simulation of the cluster, and the real thing on PostgreSQL
```

```
Starting 3 silos on PostgreSQL (logs in .bank/)...
Cluster up: 3 silos. Run 20260923-201755: opening 20 accounts with 10000 each.
Moving money for 30 s with 16 drivers; a silo is killed every 6 s.
  0s  silos ●●●  transfers      0  retried calls     0  in accounts 200000 = 200000
  2s  silos ●●●  transfers    482  retried calls     0  in accounts 188303 + 11697 in flight
  4s  silos ●●●  transfers    945  retried calls     0  in accounts 185536 + 14464 in flight
  ✗ kill -9 silo 1 (pid 13919)
  6s  silos ●○●  transfers   1355  retried calls     6  in accounts (a silo is down, asking again)
  8s  silos ●○●  transfers   1624  retried calls    13  in accounts 177804 + 22196 in flight
  ↻ restart silo 1
 10s  silos ●●●  transfers   1957  retried calls    13  in accounts 179820 + 20180 in flight
 13s  silos ●●●  transfers   2365  retried calls    22  in accounts 185781 + 14219 in flight
  ✗ kill -9 silo 2 (pid 13920)
  ↻ restart silo 2
 20s  silos ●●●  transfers   3289  retried calls    29  in accounts (a silo is down, asking again)
 23s  silos ●●●  transfers   3781  retried calls    33  in accounts 184790 + 15210 in flight
  ✗ kill -9 silo 2 (pid 13954)
 25s  silos ●●○  transfers   4307  retried calls    33  in accounts 186916 + 13084 in flight
 27s  silos ●●○  transfers   4813  retried calls    33  in accounts 185025 + 14975 in flight
  ↻ restart silo 2
 29s  silos ●●●  transfers   5290  retried calls    33  in accounts 193643 + 6357 in flight
Time. Stopping the drivers and the monkey (3 silos killed).
 33s  silos ●●●  transfers   5830  retried calls    33  in accounts 200000 = 200000
  every transfer is finished

Audit of run 20260923-201755, from PostgreSQL:
  transfers done 5154, declined for lack of funds 676, never started 0, pending 0
  62 transfers were cut off by a crash and finished later by their reminder
  money in the accounts 200000, opened with 200000
  ✓ every transfer happened exactly once, or not at all; not one öre appeared or vanished
```

The silos are separate processes (`dotnet run -- silo <i>`) and the driver
is an Orleans client. "Kill" is `Process.Kill`: no shutdown, no goodbye to
the cluster. Every silo can be killed; the database is the one thing that
has to stay.

## What is in PostgreSQL

Everything that has to survive a crash goes through Orleans' ADO.NET
providers (`glue/Postgres.cs`), into Orleans' own tables (`sql/`, the
scripts from the Orleans repository):

- cluster membership (`orleansmembershiptable`): which silos exist, so a
  restarted silo finds the others and a dead one is declared dead;
- grain storage (`orleansstorage`), named `ledger`: every account's and
  every transfer's state, one row each, with the version Orleans uses as
  its ETag;
- reminders (`orleansreminderstable`): durable timers, which is how an
  interrupted transfer gets finished.

States are written by the actor library's EDN grain storage serializer, so
a row is the Clojure value:

```
$ psql -h localhost -p 5433 -U orleans bank -c "select convert_from(payloadbinary, 'UTF8') from orleansstorage where grainidextensionstring = 'transfer/20260923-201755-t42'"
{:transfer/id "20260923-201755-t42", :transfer/status :done, :transfer/from "20260923-201755-a18", :transfer/to "20260923-201755-a13", :transfer/amount 1948}
```

## Why it holds

**An account's state is its own.** An account is an actor with
`{:persist :ledger}`: its balance and its entries are its grain's record in
the `ledger` grain storage, written by nothing but that account. There is no
`UPDATE accounts SET balance = ...` from anywhere else, and no transaction
across two accounts: money moves by messages. Orleans runs an actor one
message at a time and commits its new state before the reply leaves, so a
caller that got `:debited` knows the debit is in the database.

**Every movement is idempotent.** A debit or credit carries the id of the
transfer it belongs to, and an account records what it did for each id (its
entries). A message it has heard before is answered from its entries, not
applied again (`src/bank/account.cljr`). That is what makes retrying safe,
and after a crash everything gets retried.

**A transfer is a saga with a durable timer.** A transfer is an actor of its
own that owns the intent (`src/bank/transfer.cljr`):

1. `:transfer/start` records the order (`:pending`) and registers a
   reminder, then replies. Nothing has moved yet. The transfer id is the
   idempotency key: the drivers retry a start they did not get an answer
   to, and starting a transfer twice starts it once.
2. It debits the source and credits the destination.
3. It records the outcome (`:done`, or `:declined` when the source lacks
   the funds), and only then cancels the reminder.

A crash anywhere (the silo of the transfer or of either account killed, a
write that fails, a timeout) leaves the transfer `:pending` with its
reminder registered. The reminder is in PostgreSQL, not in the process:
when it is due, Orleans activates the transfer again on a silo that is
alive, the transfer resumes from its stored state, and step 2 runs again.
Whatever part of it had already happened is answered from the accounts'
entries. The audit counts those transfers (`:transfer/recovered?`).

**The ETag stops the one failure that is left.** While a cluster changes,
Orleans can for a moment have the same actor active on two silos, each with
the state it read. The record's version decides: the first write wins, the
second fails with `InconsistentStateException`, and that activation restarts
from what is stored. Without it, two activations of one account would each
debit from the same balance, and the second write would silently erase the
first.

## Tests

`dotnet test`:

- `test/bank/bank_test.cljr`: the saga on the actor library's local host,
  with no Orleans and no database. A transfer moves money once, is
  declined without funds, and survives a crash before or after every
  single write it causes. A stale second activation of an account cannot
  overwrite it. The audit finds what is wrong.
- **A deterministic simulation of the cluster.**
  `the-books-balance-whatever-crashes` generates everything: transfers
  started through either half of a split cluster, timer and reminder
  ticks, splits (`local/fork`: a second system on the same storage, so
  actors can be active twice), heals, restarts of the whole cluster, and
  the exact writes that crash, before or after being stored. The local
  host does nothing the test does not say, on the test's thread, so every
  case is reproducible from its seed and a failure shrinks to the smallest
  schedule that breaks. With the version check switched off, test.check finds
  the lost update within a second and shrinks it, in about another second,
  to five steps with no crash at all:

  ```clojure
  [[:split] [:transfer 0 0 1 1] [:transfer 1 0 1 1] [:tick 1] [:tick 0]]
  ;; split the cluster, start one transfer from account 0 through each
  ;; half, drive both: the account is active twice, and a write wins over
  ;; one it never saw
  ```

  This works because an actor handles one message at a time and its only
  contact with the outside world is its messages and its storage record,
  which has a small, precise contract (read with a version, write with it,
  fail when it moved). The local host implements that contract, and it
  runs the same runtime (`activate`, `handle`, `recover`) as the silos. So
  what the simulation explores, in thousands of orders, is the code the
  cluster runs, and the one thing it does not model is the thread
  scheduler.
- `test/bank/postgres_test.cljr`: the real thing, when the database is up
  (it says so and passes otherwise). An account's state is its own row, in
  EDN, and a new silo resumes it. A row written behind an activation's back
  makes its next write fail on the ETag, and the account restarts from the
  row. A short chaos run with processes killed balances the books.

## Layout

- `src/bank/model.cljr` accounts, transfers and their messages as specs.
- `src/bank/account.cljr` the account actor; `src/bank/transfer.cljr` the
  transfer saga; `src/bank/run.cljr` a run of the demo, so an audit can
  find its accounts and transfers later.
- `src/bank/audit.cljr` the books check: a pure function of the states,
  plus asking the actors for them.
- `src/bank/cluster.cljr` silos and the client on PostgreSQL, silo
  processes; `src/bank/chaos.cljr` the demo; `src/bank/main.cljr` the
  command line.
- `glue/Postgres.cs` the Orleans configuration: ADO.NET clustering, the
  `ledger` grain storage, reminders, failure detection tuned for a demo.
- `sql/` Orleans' PostgreSQL scripts (dotnet/orleans v10.3.1, `src/AdoNet`),
  run in order by `docker-compose.yml`.
- The actor library, `orleans.actors`, and its C# glue are the ants
  example's (`../orleans-actors-ants-demo`), through
  `ClojureExtraSourceDirs` and a project reference.

## Notes

- The repository's `nuget.config` lists a local `packages/` source, which a
  fresh clone does not have: `mkdir -p ../../packages` before the first
  build.
- The database is at `localhost:5433` (user and password `orleans`, database
  `bank`); `BANK_POSTGRES` takes another connection string. Each silo keeps
  a pool of at most 16 connections.
- Orleans refuses reminders more frequent than one a minute unless told
  otherwise. The demo allows one a second (a transfer retries every 5 s), and
  Orleans logs a warning about it. Failure detection is tuned the same way:
  a killed silo is declared dead within seconds instead of a minute.
- A silo started by the demo leaves the cluster gracefully when its standard
  input says `stop` or closes, so ending the demo, or killing the driver,
  leaves no silo behind. Silo logs are in `.bank/`.
- An account keeps an entry for every transfer it took part in, forever. A
  real ledger would drop entries after a retention window longer than any
  retry can take, which is how payment APIs expire their idempotency keys.
