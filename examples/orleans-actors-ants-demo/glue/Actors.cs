using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace Clojure.Orleans;

/// <summary>A message to an actor: a type name (a Clojure keyword) and an EDN payload.</summary>
[GenerateSerializer, Immutable]
public sealed record ActorMessage([property: Id(0)] string Type, [property: Id(1)] string? Payload);

/// <summary>
/// The single Orleans grain interface behind every Clojure actor. Actors are addressed by a
/// string key "type/id"; the Clojure runtime dispatches on the type.
/// </summary>
public interface IActorGrain : IGrainWithStringKey
{
    /// <summary>Request/response. The reply is EDN.</summary>
    Task<string?> Call(ActorMessage message);

    /// <summary>Fire and forget.</summary>
    [OneWay]
    Task Cast(ActorMessage message);
}

/// <summary>Implemented in Clojure (orleans.actors) and installed with <see cref="Actors.SetHost"/>.</summary>
public interface IActorHost
{
    Task OnActivate(ActorGrain grain);
    Task OnDeactivate(ActorGrain grain, string reason);
    /// <summary>Returns the EDN reply as an object (Clojure async functions return Task&lt;object&gt;).</summary>
    Task<object?> OnCall(ActorGrain grain, ActorMessage message);
    Task OnCast(ActorGrain grain, ActorMessage message);
    Task OnInfo(ActorGrain grain, ActorMessage message);
    Task OnError(ActorGrain grain, Exception error);
}

/// <summary>The generic grain. State and behaviour belong to the Clojure runtime.</summary>
public sealed class ActorGrain : Grain, IActorGrain
{
    /// <summary>The actor's state, owned by the Clojure runtime.</summary>
    public object? State { get; set; }

    public string Key => this.GetPrimaryKeyString();

    public IGrainFactory Factory => GrainFactory;

    private static IActorHost Host =>
        Actors.Host ?? throw new InvalidOperationException("The Clojure actor runtime is not started: call orleans.actors/start! first.");

    public override Task OnActivateAsync(CancellationToken cancellationToken) => Host.OnActivate(this);

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken) =>
        Host.OnDeactivate(this, reason.ReasonCode.ToString());

    public async Task<string?> Call(ActorMessage message)
    {
        try
        {
            return (string?)await Host.OnCall(this, message);
        }
        catch (Exception e)
        {
            await Host.OnError(this, e);
            throw;
        }
    }

    public async Task Cast(ActorMessage message)
    {
        try
        {
            await Host.OnCast(this, message);
        }
        catch (Exception e)
        {
            await Host.OnError(this, e);
            throw;
        }
    }

    /// <summary>Delivers <paramref name="message"/> to the actor's info handler after <paramref name="due"/>,
    /// then every <paramref name="period"/> (Timeout.InfiniteTimeSpan for a single delivery).
    /// Timer callbacks run like messages: never concurrently with other handlers.</summary>
    private readonly List<IDisposable> timers = new();

    public IDisposable Timer(ActorMessage message, TimeSpan due, TimeSpan period)
    {
        var timer = RegisterTimer(message, due, period);
        timers.Add(timer);
        return timer;
    }

    /// <summary>Cancels the actor's timers; used when the actor is restarted after an error.</summary>
    public void ClearTimers()
    {
        foreach (var timer in timers) timer.Dispose();
        timers.Clear();
    }

    private IDisposable RegisterTimer(ActorMessage message, TimeSpan due, TimeSpan period) =>
        this.RegisterGrainTimer(
            async (CancellationToken _) =>
            {
                try
                {
                    await Host.OnInfo(this, message);
                }
                catch (Exception e)
                {
                    await Host.OnError(this, e);
                    throw;
                }
            },
            new GrainTimerCreationOptions { DueTime = due, Period = period, Interleave = false });

    /// <summary>Deactivates the actor once the current message is handled. It is re-activated
    /// (with a fresh init) by the next message sent to it.</summary>
    public void Stop() => DeactivateOnIdle();
}

public static class Actors
{
    public static IActorHost? Host { get; private set; }

    public static void SetHost(IActorHost host) => Host = host;

    public static IActorGrain Ref(IGrainFactory factory, string key) => factory.GetGrain<IActorGrain>(key);

    /// <summary>Starts an in-process Orleans silo (localhost clustering) and returns the host.</summary>
    public static IHost StartSilo(int siloPort, int gatewayPort, LogLevel logLevel)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(logLevel);
        builder.UseOrleans(silo => silo.UseLocalhostClustering(siloPort, gatewayPort));
        var host = builder.Build();
        host.StartAsync().GetAwaiter().GetResult();
        return host;
    }

    public static IGrainFactory GrainFactory(IHost host) => (IGrainFactory)host.Services.GetService(typeof(IGrainFactory))!;

    public static void StopSilo(IHost host)
    {
        host.StopAsync().GetAwaiter().GetResult();
        host.Dispose();
    }
}
