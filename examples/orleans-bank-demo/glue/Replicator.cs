using System.Threading.Channels;
using Npgsql;
using Npgsql.Replication;
using Npgsql.Replication.PgOutput;
using Npgsql.Replication.PgOutput.Messages;
using NpgsqlTypes;

namespace Replication.Postgres;

/// <summary>
/// A change to a row of a published table, as the WAL says it: the table ("schema.name"), the
/// operation ("insert", "update" or "delete"), the row's columns as PostgreSQL's text for them
/// (the new row, or for a delete the key the table identifies rows by; null for SQL NULL), and
/// the LSN at the end of the transaction that made it: acknowledging that LSN says the change is
/// taken. Nothing here knows what the tables mean.
/// </summary>
public sealed record Change(string Table, string Operation, IReadOnlyDictionary<string, string?> Row,
                            ulong Lsn, DateTime CommittedAt);

/// <summary>
/// Reads the changes to the tables of a publication out of PostgreSQL's WAL, through a logical
/// replication slot (pgoutput). The slot is the cursor and PostgreSQL keeps it: a replicator
/// started on it resumes after the last LSN acknowledged through it, by this process or any
/// other, so acknowledging late can only repeat changes, never lose one. A slot has one reader at
/// a time; PostgreSQL refuses a second.
/// <para>A background pump reads the stream (and answers PostgreSQL's keepalives, so it does not
/// depend on how often changes are taken) into a bounded channel; <see cref="Drain"/> takes them
/// out, and only whole transactions go in. When the pump fails, the replicator stops and
/// <see cref="Fault"/> says why; the owner closes it and starts another.</para>
/// </summary>
public sealed class Replicator : IAsyncDisposable
{
    private readonly LogicalReplicationConnection connection;
    private readonly Channel<Change> changes;
    private readonly CancellationTokenSource stopping = new();
    private readonly Task pump;
    private ulong acknowledged;

    private Replicator(LogicalReplicationConnection connection, string slot, string publication, int capacity)
    {
        this.connection = connection;
        changes = Channel.CreateBounded<Change>(new BoundedChannelOptions(capacity) { SingleReader = true, SingleWriter = true });
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

    private static string TableOf(RelationMessage relation) => $"{relation.Namespace}.{relation.RelationName}";

    /// <summary>The columns of a tuple as text, in the relation's order. The message objects are
    /// reused by the next message, so the values are copied out now.</summary>
    private static async Task<Dictionary<string, string?>> RowOf(RelationMessage relation, ReplicationTuple tuple, CancellationToken ct)
    {
        var row = new Dictionary<string, string?>();
        var i = 0;
        await foreach (var value in tuple)
        {
            var name = relation.Columns[i++].ColumnName;
            if (value.IsDBNull)
            {
                row[name] = null;
            }
            else
            {
                using var reader = value.GetTextReader();
                row[name] = await reader.ReadToEndAsync(ct);
            }
        }
        return row;
    }

    private async Task Pump(string slot, string publication, CancellationToken ct)
    {
        try
        {
            var transaction = new List<(string Table, string Operation, Dictionary<string, string?> Row)>();
            var options = new PgOutputReplicationOptions(publication, PgOutputProtocolVersion.V1);
            await foreach (var message in connection.StartReplication(new PgOutputReplicationSlot(slot), options, ct))
            {
                switch (message)
                {
                    case BeginMessage:
                        transaction.Clear();
                        break;
                    case InsertMessage insert:
                        transaction.Add((TableOf(insert.Relation), "insert", await RowOf(insert.Relation, insert.NewRow, ct)));
                        break;
                    case UpdateMessage update:
                        transaction.Add((TableOf(update.Relation), "update", await RowOf(update.Relation, update.NewRow, ct)));
                        break;
                    case KeyDeleteMessage delete:
                        transaction.Add((TableOf(delete.Relation), "delete", await RowOf(delete.Relation, delete.Key, ct)));
                        break;
                    case FullDeleteMessage delete:
                        transaction.Add((TableOf(delete.Relation), "delete", await RowOf(delete.Relation, delete.OldRow, ct)));
                        break;
                    case CommitMessage commit:
                        var lsn = (ulong)commit.TransactionEndLsn;
                        foreach (var (table, operation, row) in transaction)
                            await changes.Writer.WriteAsync(new Change(table, operation, row, lsn, commit.TransactionCommitTimestamp), ct);
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
            changes.Writer.TryComplete();
        }
    }

    /// <summary>Takes up to <paramref name="max"/> changes that are ready, in WAL order.</summary>
    public Change[] Drain(int max)
    {
        var taken = new List<Change>();
        while (taken.Count < max && changes.Reader.TryRead(out var change))
            taken.Add(change);
        return taken.ToArray();
    }

    /// <summary>Says every change up to <paramref name="lsn"/> is taken; PostgreSQL gets it with
    /// the next status update (within a second) and may drop the WAL before it.</summary>
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
