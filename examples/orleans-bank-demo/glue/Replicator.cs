using System.Threading.Channels;
using Npgsql;
using Npgsql.Replication;
using Npgsql.Replication.PgOutput;
using Npgsql.Replication.PgOutput.Messages;
using NpgsqlTypes;

namespace Bank.Orleans;

/// <summary>A row added to ledger_entries, as the replicator read it from the WAL, with the LSN
/// at the end of the transaction that added it: acknowledging that LSN says the row is taken.</summary>
public sealed record LedgerRow(string AccountId, long Seq, string TransferId, short Kind, long Amount,
                               long BalanceAfter, DateTime BookedAt, ulong Lsn);

/// <summary>
/// Reads the rows added to the tables of a publication out of PostgreSQL's WAL, through a logical
/// replication slot (pgoutput). The slot is the cursor and PostgreSQL keeps it: a replicator
/// started on it resumes after the last LSN acknowledged through it, by this process or any
/// other, so acknowledging late can only repeat rows, never lose one. A slot has one reader at a
/// time; PostgreSQL refuses a second.
/// <para>A background pump reads the stream (and answers PostgreSQL's keepalives, so it does not
/// depend on how often rows are taken) into a bounded channel; <see cref="Drain"/> takes rows
/// out of it, and only whole transactions go in. When the pump fails, the replicator stops and
/// <see cref="Fault"/> says why; the owner disposes of it and starts another.</para>
/// </summary>
public sealed class Replicator : IAsyncDisposable
{
    private readonly LogicalReplicationConnection connection;
    private readonly Channel<LedgerRow> rows;
    private readonly CancellationTokenSource stopping = new();
    private readonly Task pump;
    private ulong acknowledged;

    private Replicator(LogicalReplicationConnection connection, string slot, string publication, int capacity)
    {
        this.connection = connection;
        rows = Channel.CreateBounded<LedgerRow>(new BoundedChannelOptions(capacity) { SingleReader = true, SingleWriter = true });
        pump = Task.Run(() => Pump(slot, publication, stopping.Token));
    }

    /// <summary>Why the pump stopped, or null while it runs.</summary>
    public Exception? Fault { get; private set; }

    /// <summary>True while the pump reads the stream.</summary>
    public bool Running => !pump.IsCompleted;

    /// <summary>The last LSN acknowledged through this replicator.</summary>
    public ulong Acknowledged => Interlocked.Read(ref acknowledged);

    /// <summary>Opens a replication connection and starts reading the slot, creating the slot (from
    /// now on) when it does not exist.</summary>
    public static async Task<Replicator> StartAsync(string connectionString, string slot, string publication, int capacity)
    {
        var connection = new LogicalReplicationConnection(connectionString)
        {
            WalReceiverStatusInterval = TimeSpan.FromSeconds(1),
        };
        await connection.Open();
        try
        {
            await connection.CreatePgOutputReplicationSlot(slot, slotSnapshotInitMode: LogicalSlotSnapshotInitMode.NoExport);
        }
        catch (PostgresException e) when (e.SqlState == "42710")
        {
            // the slot exists: resume from it
        }
        return new Replicator(connection, slot, publication, capacity);
    }

    private async Task Pump(string slot, string publication, CancellationToken ct)
    {
        try
        {
            var transaction = new List<LedgerRow>();
            var options = new PgOutputReplicationOptions(publication, PgOutputProtocolVersion.V1);
            await foreach (var message in connection.StartReplication(new PgOutputReplicationSlot(slot), options, ct))
            {
                switch (message)
                {
                    case BeginMessage:
                        transaction.Clear();
                        break;
                    case InsertMessage insert when insert.Relation.RelationName == "ledger_entries":
                        // the message object is reused: copy the values out now, in the order of
                        // the relation's columns, from their text (pgoutput sends text)
                        var text = new Dictionary<string, string>();
                        var i = 0;
                        await foreach (var value in insert.NewRow)
                        {
                            var name = insert.Relation.Columns[i++].ColumnName;
                            using var reader = value.GetTextReader();
                            text[name] = await reader.ReadToEndAsync(ct);
                        }
                        transaction.Add(new LedgerRow(text["account_id"], long.Parse(text["seq"]), text["transfer_id"],
                                                      short.Parse(text["kind"]), long.Parse(text["amount"]),
                                                      long.Parse(text["balance_after"]),
                                                      DateTimeOffset.Parse(text["booked_at"], System.Globalization.CultureInfo.InvariantCulture).UtcDateTime,
                                                      0));
                        break;
                    case CommitMessage commit:
                        var lsn = (ulong)commit.TransactionEndLsn;
                        foreach (var row in transaction)
                            await rows.Writer.WriteAsync(row with { Lsn = lsn }, ct);
                        transaction.Clear();
                        break;
                }
            }
            Fault = new InvalidOperationException("The replication stream ended");
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            Fault = e;
        }
        finally
        {
            rows.Writer.TryComplete();
        }
    }

    /// <summary>Takes up to <paramref name="max"/> rows that are ready, in WAL order.</summary>
    public LedgerRow[] Drain(int max)
    {
        var taken = new List<LedgerRow>();
        while (taken.Count < max && rows.Reader.TryRead(out var row))
            taken.Add(row);
        return taken.ToArray();
    }

    /// <summary>Says every row up to <paramref name="lsn"/> is taken; PostgreSQL gets it with the
    /// next status update (within a second) and may drop the WAL before it.</summary>
    public void Acknowledge(ulong lsn)
    {
        if (lsn <= Acknowledged) return;
        Interlocked.Exchange(ref acknowledged, lsn);
        connection.SetReplicationStatus(new NpgsqlLogSequenceNumber(lsn));
    }

    /// <summary>Stops the pump and closes the connection: <see cref="DisposeAsync"/> as a Task.</summary>
    public Task CloseAsync() => DisposeAsync().AsTask();

    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();
        try { await pump; } catch { /* stopping */ }
        await connection.DisposeAsync();
        stopping.Dispose();
    }
}
