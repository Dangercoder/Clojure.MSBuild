using Orleans.Configuration;
using Orleans.Hosting;

namespace Bank.Orleans;

/// <summary>
/// The cluster on PostgreSQL: membership, the "ledger" grain storage and reminders, all through
/// Orleans' ADO.NET providers, so everything that has to survive a crash is committed to the
/// database. The tables are Orleans' own (sql/). Given to orleans.actors.silo/start as :configure
/// and to orleans.actors.silo/connect.
/// </summary>
public static class Postgres
{
    public const string Invariant = "Npgsql";
    public const string ClusterId = "clojure-bank";
    public const string ServiceId = "clojure-bank";

    /// <summary>A silo of the bank's cluster. Failure detection is tuned for a demo that kills
    /// silos on purpose: a dead silo is declared dead within a few seconds instead of a minute.</summary>
    public static Action<ISiloBuilder> Silo(string connectionString, TimeSpan minimumReminderPeriod) => silo =>
    {
        silo.Configure<ClusterOptions>(o =>
        {
            o.ClusterId = ClusterId;
            o.ServiceId = ServiceId;
        });
        silo.UseAdoNetClustering(o =>
        {
            o.Invariant = Invariant;
            o.ConnectionString = connectionString;
        });
        silo.AddAdoNetGrainStorage("ledger", o =>
        {
            o.Invariant = Invariant;
            o.ConnectionString = connectionString;
        });
        silo.UseAdoNetReminderService(o =>
        {
            o.Invariant = Invariant;
            o.ConnectionString = connectionString;
        });
        silo.Configure<ReminderOptions>(o => o.MinimumReminderPeriod = minimumReminderPeriod);
        silo.Configure<ClusterMembershipOptions>(o =>
        {
            o.ProbeTimeout = TimeSpan.FromSeconds(1);
            o.NumMissedProbesLimit = 2;
            o.NumVotesForDeathDeclaration = 1;
            o.TableRefreshTimeout = TimeSpan.FromSeconds(2);
            o.IAmAliveTablePublishTimeout = TimeSpan.FromSeconds(5);
            o.DeathVoteExpirationTimeout = TimeSpan.FromSeconds(10);
        });
        silo.Configure<SiloMessagingOptions>(o =>
        {
            o.ResponseTimeout = TimeSpan.FromSeconds(5);
            o.SystemResponseTimeout = TimeSpan.FromSeconds(5);
        });
    };

    /// <summary>A client of the bank's cluster: finds the silos' gateways in the membership table.</summary>
    public static Action<IClientBuilder> Client(string connectionString) => client =>
    {
        client.Configure<ClusterOptions>(o =>
        {
            o.ClusterId = ClusterId;
            o.ServiceId = ServiceId;
        });
        client.UseAdoNetClustering(o =>
        {
            o.Invariant = Invariant;
            o.ConnectionString = connectionString;
        });
        client.Configure<GatewayOptions>(o => o.GatewayListRefreshPeriod = TimeSpan.FromSeconds(2));
        client.Configure<ClientMessagingOptions>(o => o.ResponseTimeout = TimeSpan.FromSeconds(5));
    };
}
