# A bank that survives `kill -9`: Orleans on PostgreSQL, from ClojureCLR

Accounts and transfers as Clojure actors on a cluster of
[Microsoft Orleans](https://learn.microsoft.com/dotnet/orleans/) silos, with
everything durable in PostgreSQL. An account keeps an append-only journal,
one ledger row per entry with the balance after it, so its balance at any
moment in the past is one index lookup, whether it has ten entries or a
hundred million. Transfers are kept by Orleans' own grain storage. A chaos
monkey kills a silo process every few seconds while thousands of transfers
are in flight. At the end an audit reads the books back from the database:
every transfer happened exactly once or not at all, every journal explains
its balance entry by entry, and the books balanced at every moment of the
run, not only at the end. Meanwhile a replicator, an actor too, streams
every ledger row out of PostgreSQL's WAL to a sink in the driver's
process; the monkey kills its silo first, and every row still arrives.

```
docker compose up -d        # PostgreSQL 17 with Orleans' tables (sql/)
dotnet run                  # 3 silos, 20 accounts, 30 s of transfers, a kill -9 every 6 s
dotnet run -- chaos --silos 4 --accounts 50 --seconds 60 --kill-every 4 --workers 32
dotnet run -- audit <run>   # audit a past run in a fresh process, from nothing but the database
dotnet run -- bench         # one account with a million entries (--entries n for more): activation, balance at a moment
dotnet test                 # the saga, a deterministic simulation of the cluster, and the real thing on PostgreSQL
```

```
Starting 3 silos on PostgreSQL (logs in .bank/)...
Cluster up: 3 silos, the replicator streaming. Run 20260924-060136: opening 20 accounts with 10000 each.
Moving money for 30 s with 16 drivers; a silo is killed every 6 s.
  0s  silos ●●●  transfers      0  retried calls     0  in accounts 200000 = 200000
  2s  silos ●●●  transfers    525  retried calls     0  in accounts 197952 + 2048 in flight
  4s  silos ●●●  transfers   1131  retried calls     0  in accounts 192453 + 7547 in flight
  ✗ kill -9 silo 0 (pid 43668), the replicator's
  ↻ restart silo 0
 11s  silos ●●●  transfers   2800  retried calls     5  in accounts (a silo is down, asking again)
 13s  silos ●●●  transfers   3269  retried calls    13  in accounts 195513 + 4487 in flight
  ✗ kill -9 silo 2 (pid 43670)
 15s  silos ●●○  transfers   3846  retried calls    13  in accounts (a silo is down, asking again)
 17s  silos ●●○  transfers   4144  retried calls    17  in accounts 188994 + 11006 in flight
  ↻ restart silo 2
 19s  silos ●●●  transfers   4605  retried calls    17  in accounts 187271 + 12729 in flight
 21s  silos ●●●  transfers   4983  retried calls    27  in accounts 193142 + 6858 in flight
 23s  silos ●●●  transfers   5591  retried calls    27  in accounts 198861 + 1139 in flight
  ✗ kill -9 silo 1 (pid 43669)
 25s  silos ●○●  transfers   5853  retried calls    28  in accounts 197805 + 2195 in flight
  ↻ restart silo 1
 27s  silos ●●●  transfers   6132  retried calls    28  in accounts 195212 + 4788 in flight
 29s  silos ●●●  transfers   6428  retried calls    41  in accounts 196328 + 3672 in flight
Time. Stopping the drivers and the monkey (3 silos killed).
 33s  silos ●●●  transfers   7130  retried calls    41  in accounts 200000 = 200000
  every transfer is finished

The books at moments of the run, each account asked for its balance then:
  06:01:45.938307  accounts 198713 + on its way 1287 = 200000  ✓
  06:01:53.171722  accounts 192619 + on its way 7381 = 200000  ✓
  06:02:00.069552  accounts 194671 + on its way 5329 = 200000  ✓
  06:02:08.503910  accounts 189419 + on its way 10581 = 200000  ✓
  06:02:17.765755  accounts 198013 + on its way 1987 = 200000  ✓

Audit of run 20260924-060136, from PostgreSQL:
  transfers done 6469, declined for lack of funds 661, never started 20, pending 0
  31 transfers were cut off by a crash and finished later by their reminder
  money in the accounts 200000, opened with 200000
  every journal explains its balance, entry by entry; the books balanced at each of the 13566 moments something was booked
  ✓ every transfer happened exactly once, or not at all; not one öre appeared or vanished

The ledger streamed out of the WAL by the replicator, into the sink in this process:
  13619 rows booked by the run's accounts; 13619 of them in the sink, as events
  837 events came more than once (after a restart, acknowledged late) and were kept once
  the replicator ran in 3 activations, on 3 processes
  ✓ every row of the ledger reached the sink, whatever was killed
```

The silos are separate processes (`dotnet run -- silo <i>`) and the driver
is an Orleans client. "Kill" is `Process.Kill`: no shutdown, no goodbye to
the cluster. Every silo can be killed; the database is the one thing that
has to stay.

## What is in PostgreSQL

Everything that has to survive a crash is committed to the database:

- the accounts' journals (`ledger_entries`, `sql/09-ledger.sql`), the
  bank's own table: one row per entry, never updated;
- grain storage (`orleansstorage`), named `bank`: every transfer's and
  every run's state, one row each, with the version Orleans uses as its
  ETag;
- cluster membership (`orleansmembershiptable`): which silos exist, so a
  restarted silo finds the others and a dead one is declared dead;
- reminders (`orleansreminderstable`): durable timers, which is how an
  interrupted transfer gets finished.

The last three are Orleans' ADO.NET providers (`glue/Postgres.cs`) and
Orleans' own tables (`sql/01`–`08`, the scripts from the Orleans
repository). An account's journal is rows:

```
$ psql -h localhost -p 5433 -U orleans bank -c "select seq, transfer_id, kind, amount, balance_after, booked_at from ledger_entries where account_id = '20260923-205429-a3' order by seq limit 5"
 seq |       transfer_id       | kind | amount | balance_after |           booked_at
-----+-------------------------+------+--------+---------------+-------------------------------
   1 | 20260923-205429-a3-open |    0 |  10000 |         10000 | 2026-09-23 20:54:39.360872+00
   2 | 20260923-205429-t60     |    1 |    104 |          9896 | 2026-09-23 20:54:39.886476+00
   3 | 20260923-205429-t130    |    2 |    345 |         10241 | 2026-09-23 20:54:39.983214+00
   4 | 20260923-205429-t132    |    1 |   1115 |          9126 | 2026-09-23 20:54:39.99846+00
   5 | 20260923-205429-t52     |    1 |   2118 |          7008 | 2026-09-23 20:54:40.011214+00
```

and a state in grain storage is the Clojure value, written by the actor
library's EDN grain storage serializer:

```
$ psql -h localhost -p 5433 -U orleans bank -c "select convert_from(payloadbinary, 'UTF8') from orleansstorage where grainidextensionstring = 'transfer/20260923-205429-t60'"
{:transfer/id "20260923-205429-t60", :transfer/status :done, :transfer/from "20260923-205429-a3", :transfer/to "20260923-205429-a1", :transfer/amount 104, :transfer/debited-at 1790196879886476, :transfer/credited-at 1790196880036079}
```

## Why it holds

**An account's journal is its own.** An account is a journaled actor
(`{:journal :ledger}`, `src/bank/account.cljr`): what happened to it is its
rows in `ledger_entries`, appended by nothing but that account. There is no
`UPDATE accounts SET balance = ...` from anywhere, and no transaction across
two accounts: money moves by messages. Orleans runs an actor one message at
a time, and the account appends its entry before it replies, so a caller
that got a debit entry knows the debit is in the database.

**Every movement is idempotent.** A debit or credit carries the id of the
transfer it belongs to, and the journal has one entry per transfer id at
most (`unique (account_id, transfer_id)`). A message the account has heard
before appends nothing: the insert finds the id and the account answers
with the entry it booked then. That is what makes retrying safe, and after
a crash everything gets retried.

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

**The seq and the ETag stop the one failure that is left.** While a cluster
changes, Orleans can for a moment have the same actor active on two silos,
each with the state it read. For an account, the entry's seq decides: both
activations append the seq after the end they read, the primary key
`(account_id, seq)` takes the first, and the second fails and restarts
from the journal. For a transfer in grain storage, the record's version does
the same: the second write fails with `InconsistentStateException`. Without
them, two activations of one account would each debit from the same
balance, and the second would silently erase the first.

## The balance at any moment

Because an account is the only writer of its journal, it knows its next seq
and its next balance without asking anyone, so every row can carry
`balance_after`, the running balance, at no cost: no lock, no read before
the write. That makes the balance at a moment the last row booked at or
before it:

```sql
SELECT balance_after FROM ledger_entries
WHERE account_id = $1 AND booked_at <= $2
ORDER BY booked_at DESC LIMIT 1;
```

one descent of the `(account_id, booked_at)` index, which is the account's
`:account/balance-at` (through `orleans.actors/entry-at`). An account's
state in memory is only where its journal ends (seq, balance, time), so
activating it reads one row too, however long its history.

`dotnet run -- bench` loads an account with a million entries (one credit a
second from 2020 on, straight into the table with `generate_series`) and
asks it, through the actor on a silo:

```
ledger_entries: 210 MB on disk, of which 1000000 rows are bench-1000000.
Activating the account (resume reads its last entry): 14,7 ms
Balance at 1000 random moments between 2020 and 2023: p50 1,56 ms, p99 2,77 ms, max 16,19 ms, every one right
An old entry by its transfer id, 1000 times: p50 1,62 ms, p99 2,24 ms, max 19,72 ms, every one right
Booking one more credit: 76,0 ms, seq 1000001, balance 1000005

  Limit  (actual time=0.073..0.074 rows=1 loops=1)
    Buffers: shared hit=4
    ->  Index Scan Backward using ledger_entries_booked_at on ledger_entries
  Execution Time: 0.108 ms
```

The query reads four index pages; most of the 1.5 ms is the trip through
Orleans and Npgsql. A B-tree over a hundred million rows of one account is
one or two levels deeper than over a million, so the lookup should stay in
the same range; `--entries 100000000` runs the same measurement at that
size, which is not in this README (its rows alone are 9 GB before the
indexes are built).

**One moment for the whole bank.** Between a transfer's debit and its
credit the money is in no account, so the accounts' balances at a moment
add up to what was deposited minus what was on its way then. That only
works if a credit is never booked before its debit, and the silos' clocks
disagree. So an entry is booked at `max(the silo's clock, the account's
previous entry + 1 µs, the time of the entry that caused it)`
(`orleans.actors/next-at`): the credit message carries the debit's time.
With that, the entries of all accounts up to any moment are a consistent
cut. The audit sweeps every entry of the run in time order and checks,
at each moment something was booked, that the accounts plus the money on
its way hold what was deposited. The demo also asks the accounts for their
balances at five moments of the run (the books at the end of the output).

**What this does not do.** `booked_at` is when the bank recorded an entry.
A bank that books entries with a value date in the past needs a second
time axis (bitemporal data). An account that takes thousands of entries a
second would need its appends batched, since an actor books one message at
a time, or to be split into sub-accounts. A ledger with years of history
would be partitioned by month, which keeps the lookup one descent.


## Streaming the ledger out of the WAL

Every row the accounts add to the ledger is also an event, and the
replicator (`src/bank/events.cljr`) streams them out of PostgreSQL itself,
without a second write and without another process: logical replication
(`wal_level=logical`) through a slot, `bank_events`, on the publication of
`ledger_entries` (`sql/10-events.sql`).

```clojure
#:event{:type :account/debited :account "<run>-a3" :seq 42 :transfer "<run>-t7"
        :amount 300 :balance 9700 :at <µs since the epoch> :lsn <WAL position of its commit>}
```

- **The slot is the cursor, and PostgreSQL keeps it.** A consumer takes
  events (`:events/take`, the oldest first, as often as it likes) and
  acknowledges what it has kept (`:events/ack` with the last `:event/lsn`),
  which moves the slot on. Whatever happens to the replicator, the next one
  starts right after the last acknowledged event: an event acknowledged
  late comes again, none is lost, and the consumer keeps each once by
  `(account, seq)`. PostgreSQL keeps the WAL the slot has not acknowledged,
  up to `max_slot_wal_keep_size`.
- **It does not stay dead.** A reminder wakes the actor every 5 s on
  whichever silo is alive, and a wake starts the stream when it is not
  running: after its silo was killed, after the stream failed (the
  connection dropped, PostgreSQL restarted it, `57P01`), or after the actor
  itself failed and restarted, `terminate` (new in the actor library, the
  GenServer `terminate/2`) closing the old stream first.
- **One reader.** A slot takes one reader at a time and PostgreSQL refuses a
  second, so two activations of the replicator cannot both read. A reader
  that died lets go of the slot within `wal_sender_timeout` (5 s here).
- **Whole transactions, from its own pump.** The C# `glue/Replicator.cs`
  reads Npgsql's pgoutput stream on a background task (which also answers
  PostgreSQL's keepalives) into a bounded channel, only whole transactions,
  each row with the LSN at its transaction's end; the actor moves them into
  its buffer every 100 ms.

In the demo the sink is memory in the driver (`src/bank/sink.cljr`),
outside the silos the monkey kills, where a broker would be; a consumer
thread drains the replicator into it all run long. The monkey's first kill
is the replicator's silo. At the end the driver checks the sink against
every account's journal: every row there, once, the repeats identical.

## Tests

`dotnet test`:

- `test/bank/bank_test.cljr`: the saga on the actor library's local host,
  with no Orleans and no database. A transfer moves money once, is
  declined without funds, and survives a crash before or after every
  single write it causes. An account answers a repeated message from its
  journal, comes back from its last entry, and gives its balance at any
  moment. A credit is never booked before its debit, even on a silo whose
  clock is a second behind. A stale second activation cannot append past
  the journal, nor overwrite a transfer's record. The audit finds what is
  wrong.
- **A deterministic simulation of the cluster.**
  `the-books-balance-whatever-crashes` generates everything: transfers
  started through either half of a split cluster, timer and reminder
  ticks, splits (`local/fork`: a second system on the same storage and
  journals, so actors can be active twice, with a clock up to five seconds
  off), heals, restarts of the whole cluster, and the exact writes that
  crash, before or after being stored. After each case the audit checks
  the books at every moment of it. The local
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

  The same with the journal's seq check switched off:
  `[[:split 0] [:transfer 0 0 1 1] [:tick 1] [:transfer 0 0 1 1] [:tick 0]]`.
  And with the credit ignoring its debit's time, test.check finds that a
  clock two milliseconds ahead is enough for a credit to be booked before
  its debit, so that for a moment the money is in both accounts:
  `[[:split 2000] [:transfer 0 0 1 1] [:tick 1] [:transfer 0 1 1 1]]`.

  This works because an actor handles one message at a time and its only
  contact with the outside world is its messages and its storage record,
  which has a small, precise contract (read with a version, write with it,
  fail when it moved). The local host implements that contract, and it
  runs the same runtime (`activate`, `handle`, `recover`) as the silos. So
  what the simulation explores, in thousands of orders, is the code the
  cluster runs, and the one thing it does not model is the thread
  scheduler.
- `test/bank/postgres_test.cljr`: the real thing, when the database is up
  (it says so and passes otherwise). An account's journal is its rows in
  `ledger_entries`, a new silo resumes it from the last one, and its
  balance at a moment comes from the table. A row appended behind an
  activation's back makes its next append fail on the seq, and the account
  restarts from the table. A transfer's or run's state is its row in grain
  storage, in EDN, and a row written behind its back makes the next write
  fail on the ETag. The replicator streams an account's rows, starts its
  stream again when PostgreSQL kills the connection, and hands over from
  the slot to the replicator of the next silo, nothing missing. A short
  chaos run with processes killed balances the books and streams every
  row.

## Layout

- `src/bank/model.cljr` accounts, transfers and their messages as specs.
- `src/bank/account.cljr` the account actor and its journal;
  `src/bank/ledger.cljr` the journal in PostgreSQL; `src/bank/transfer.cljr` the
  transfer saga; `src/bank/run.cljr` a run of the demo, so an audit can
  find its accounts and transfers later.
- `src/bank/audit.cljr` the books check: a pure function of the journals
  and states, plus asking the actors for them.
- `src/bank/cluster.cljr` silos and the client on PostgreSQL, silo
  processes; `src/bank/chaos.cljr` the demo; `src/bank/bench.cljr` the
  million-entry account; `src/bank/events.cljr` the replicator,
  `src/bank/sink.cljr` the sink; `src/bank/main.cljr` the command line.
- `glue/Postgres.cs` the Orleans configuration: ADO.NET clustering, the
  `bank` grain storage, reminders, failure detection tuned for a demo;
  `glue/Replicator.cs` the WAL reader.
- `sql/` Orleans' PostgreSQL scripts (dotnet/orleans v10.3.1, `src/AdoNet`)
  and the bank's `09-ledger.sql` and `10-events.sql`, run in order by
  `docker-compose.yml`. A database created before them needs them run by
  hand (and `docker compose up -d` for the new PostgreSQL settings), or
  `docker compose down -v` and up again.
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
- The replicator's reminder stays registered between runs, so it wakes on
  the next run's silos and carries on from the slot; `:events/stop` stops
  it for good, and `SELECT pg_drop_replication_slot('bank_events')` lets the
  WAL go.
- A silo started by the demo leaves the cluster gracefully when its standard
  input says `stop` or closes, so ending the demo, or killing the driver,
  leaves no silo behind. Silo logs are in `.bank/`.
- Orleans has its own event-sourcing API, `JournaledGrain` with a custom
  storage provider, for the same idea. The actor library keeps one grain
  class for every actor type, so its journal is a host capability instead.
- `bench` drops the table's constraints while it loads and builds them
  again after: run it when nothing else uses the database. Its rows are
  ledger rows, so the replicator streams them too, the next time it runs. Its account
  stays in the table; `DELETE FROM ledger_entries WHERE account_id LIKE
  'bench-%'` removes it.
