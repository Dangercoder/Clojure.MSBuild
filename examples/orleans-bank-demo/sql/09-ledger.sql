-- The bank's own table, next to Orleans' ones: every account's journal.
-- An account appends one row per thing that happened to it and never
-- updates one; it is the only writer of its rows (Orleans activates it once),
-- which is what makes seq and balance_after cheap: the account knows both.
--
--   seq            1, 2, 3 ... per account, no gaps. The primary key is the
--                  fence: a second activation of the account that read an
--                  older end of the journal cannot append the same seq.
--   transfer_id    the idempotency key: a transfer moves an account once.
--   kind           0 deposit, 1 debit, 2 credit, 3 declined (a debit refused
--                  for lack of funds, remembered so it is refused again).
--   balance_after  the running balance, so the balance at any moment is one
--                  row: the last one booked at or before it.
--   booked_at      increasing per account; a credit is never booked before
--                  the debit it comes from.
CREATE TABLE ledger_entries
(
    account_id    text        NOT NULL,
    seq           bigint      NOT NULL,
    transfer_id   text        NOT NULL,
    kind          smallint    NOT NULL,
    amount        bigint      NOT NULL,
    balance_after bigint      NOT NULL,
    booked_at     timestamptz NOT NULL,
    CONSTRAINT ledger_entries_pkey PRIMARY KEY (account_id, seq),
    CONSTRAINT ledger_entries_transfer UNIQUE (account_id, transfer_id)
);

CREATE INDEX ledger_entries_booked_at ON ledger_entries (account_id, booked_at);
