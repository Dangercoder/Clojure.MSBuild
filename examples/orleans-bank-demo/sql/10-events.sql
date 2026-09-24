-- What the replicator (bank.events) streams out of the WAL: every row added to
-- the ledger. The slot it reads through, bank_events, is created by the
-- replicator when it first starts.
CREATE PUBLICATION bank_events FOR TABLE ledger_entries WITH (publish = 'insert');
