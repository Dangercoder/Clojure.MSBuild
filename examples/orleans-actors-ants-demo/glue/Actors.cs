using System.Buffers;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.WireProtocol;

namespace Clojure.Orleans;

/// <summary>
/// A message to an actor: a type name (a Clojure keyword) and a payload, any Clojure value.
/// Immutable, so Orleans hands the payload over by reference inside a silo; between silos
/// it goes through <see cref="ClojureCodec"/>.
/// </summary>
[GenerateSerializer, Immutable]
public sealed record ActorMessage([property: Id(0)] string Type, [property: Id(1)] object? Payload);

/// <summary>The reply to a call: the value, or the data describing why the message was rejected.</summary>
[GenerateSerializer, Immutable]
public sealed record ActorReply([property: Id(0)] bool Ok, [property: Id(1)] object? Value);

/// <summary>
/// The single Orleans grain interface behind every Clojure actor. Actors are addressed by a
/// string key "type/id"; the Clojure runtime dispatches on the type.
/// </summary>
public interface IActorGrain : IGrainWithStringKey
{
    /// <summary>Request/response.</summary>
    Task<ActorReply> Call(ActorMessage message);

    /// <summary>Fire and forget.</summary>
    [OneWay]
    Task Cast(ActorMessage message);
}

/// <summary>Implemented in Clojure (orleans.actors.silo) and given to <see cref="Actors.StartSiloAsync"/>;
/// grains receive it through dependency injection.</summary>
public interface IActorHost
{
    Task OnActivate(ActorGrain grain);
    Task OnDeactivate(ActorGrain grain, string reason);
    /// <summary>Returns an <see cref="ActorReply"/> (Clojure async functions return Task&lt;object&gt;).</summary>
    Task<object?> OnCall(ActorGrain grain, ActorMessage message);
    Task OnCast(ActorGrain grain, ActorMessage message);
    Task OnInfo(ActorGrain grain, ActorMessage message);
    Task OnError(ActorGrain grain, Exception error);
}

/// <summary>How Clojure values are written to and read from the wire between silos. Implemented
/// in Clojure (EDN) and given to <see cref="Actors.StartSiloAsync"/>.</summary>
public interface IClojureWire
{
    string Write(object value);
    object Read(string text);
}

/// <summary>The generic grain. State and behaviour belong to the Clojure runtime; the grain keeps
/// the state slot and the timers.</summary>
public sealed class ActorGrain : Grain, IActorGrain
{
    private readonly IActorHost host;
    private readonly List<IDisposable> timers = new();

    public ActorGrain(IActorHost host) => this.host = host;

    /// <summary>The actor's state, owned by the Clojure runtime.</summary>
    public object? State { get; set; }

    public string Key => this.GetPrimaryKeyString();

    public IGrainFactory Factory => GrainFactory;

    public override Task OnActivateAsync(CancellationToken cancellationToken) => host.OnActivate(this);

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken) =>
        host.OnDeactivate(this, reason.ReasonCode.ToString());

    public async Task<ActorReply> Call(ActorMessage message)
    {
        try
        {
            return (ActorReply)(await host.OnCall(this, message))!;
        }
        catch (Exception e)
        {
            await host.OnError(this, e);
            throw;
        }
    }

    public async Task Cast(ActorMessage message)
    {
        try
        {
            await host.OnCast(this, message);
        }
        catch (Exception e)
        {
            await host.OnError(this, e);
            throw;
        }
    }

    /// <summary>Delivers <paramref name="message"/> to the actor's info handler after <paramref name="due"/>,
    /// then every <paramref name="period"/> (Timeout.InfiniteTimeSpan for a single delivery).
    /// Timer callbacks run like messages: never concurrently with other handlers.</summary>
    public IDisposable Timer(ActorMessage message, TimeSpan due, TimeSpan period)
    {
        var timer = this.RegisterGrainTimer(
            async (CancellationToken _) =>
            {
                try
                {
                    await host.OnInfo(this, message);
                }
                catch (Exception e)
                {
                    await host.OnError(this, e);
                    throw;
                }
            },
            new GrainTimerCreationOptions { DueTime = due, Period = period, Interleave = false });
        timers.Add(timer);
        return timer;
    }

    /// <summary>Cancels the actor's timers; used when the actor is restarted after an error.</summary>
    public void ClearTimers()
    {
        foreach (var timer in timers) timer.Dispose();
        timers.Clear();
    }

    /// <summary>Deactivates the actor once the current message is handled. It is re-activated
    /// (with a fresh init, or a resume) by the next message sent to it.</summary>
    public void Stop() => DeactivateOnIdle();
}

/// <summary>Marker type written in the field header of a Clojure value on the wire, so the
/// reading side hands the field to <see cref="ClojureCodec"/>.</summary>
public sealed class ClojureValue { }

/// <summary>
/// Serializes Clojure values between silos through <see cref="IClojureWire"/>. Inside a silo
/// Orleans never serializes; there Clojure values are passed by reference, being immutable.
/// </summary>
public sealed class ClojureCodec : IGeneralizedCodec
{
    private readonly IClojureWire wire;

    public ClojureCodec(IClojureWire wire) => this.wire = wire;

    public static bool IsClojureType(Type type) =>
        type == typeof(ClojureValue)
        || typeof(clojure.lang.IPersistentCollection).IsAssignableFrom(type)
        || typeof(clojure.lang.IFn).IsAssignableFrom(type)
        || type == typeof(clojure.lang.Keyword)
        || type == typeof(clojure.lang.Symbol)
        || type == typeof(clojure.lang.BigInt)
        || type == typeof(clojure.lang.Ratio);

    public bool IsSupportedType(Type type) => IsClojureType(type);

    public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, object value)
        where TBufferWriter : IBufferWriter<byte>
    {
        if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
            return;

        var bytes = Encoding.UTF8.GetBytes(wire.Write(value));
        writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(ClojureValue), WireType.LengthPrefixed);
        writer.WriteVarUInt32((uint)bytes.Length);
        writer.Write(bytes);
    }

    public object ReadValue<TInput>(ref Reader<TInput> reader, Field field)
    {
        if (field.IsReference)
            return ReferenceCodec.ReadReference<object, TInput>(ref reader, field);

        field.EnsureWireType(WireType.LengthPrefixed);
        var length = reader.ReadVarUInt32();
        var bytes = reader.ReadBytes(length);
        var value = wire.Read(Encoding.UTF8.GetString(bytes));
        ReferenceCodec.RecordObject(reader.Session, value);
        return value;
    }
}

/// <summary>Clojure values are immutable: a deep copy is the value itself.</summary>
public sealed class ClojureCopier : IGeneralizedCopier
{
    public bool IsSupportedType(Type type) => ClojureCodec.IsClojureType(type);

    public object? DeepCopy(object? input, CopyContext context) => input;
}

public static class Actors
{
    public static IActorGrain Ref(IGrainFactory factory, string key) => factory.GetGrain<IActorGrain>(key);

    /// <summary>
    /// Starts an Orleans silo in this process and returns the host. The actor host and the wire
    /// are registered as services: grains and the codec get them injected, nothing is static.
    /// Localhost clustering: the silo is the primary of its cluster unless
    /// <paramref name="primarySiloPort"/> names the silo to join. Clustering across machines is
    /// a membership provider here instead.
    /// </summary>
    public static async Task<IHost> StartSiloAsync(IActorHost actorHost, IClojureWire wire, int siloPort, int gatewayPort, int primarySiloPort, LogLevel logLevel)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(logLevel);
        builder.Services.AddSingleton(actorHost);
        builder.Services.AddSingleton(wire);
        builder.Services.AddSingleton<IGeneralizedCodec, ClojureCodec>();
        builder.Services.AddSingleton<IGeneralizedCopier, ClojureCopier>();
        builder.UseOrleans(silo =>
        {
            if (primarySiloPort > 0)
                silo.UseLocalhostClustering(siloPort, gatewayPort, new IPEndPoint(IPAddress.Loopback, primarySiloPort));
            else
                silo.UseLocalhostClustering(siloPort, gatewayPort);
        });
        var host = builder.Build();
        await host.StartAsync();
        return host;
    }

    public static IGrainFactory GrainFactory(IHost host) => host.Services.GetRequiredService<IGrainFactory>();

    /// <summary>The number of silos the cluster currently considers active.</summary>
    public static async Task<int> ActiveSiloCount(IHost host)
    {
        var management = GrainFactory(host).GetGrain<IManagementGrain>(0);
        var hosts = await management.GetHosts(onlyActive: true);
        return hosts.Count;
    }

    /// <summary>Serializes a message the way it would travel to another silo and reads it back.</summary>
    public static ActorMessage RoundTrip(IHost host, ActorMessage message)
    {
        var serializer = host.Services.GetRequiredService<Serializer>();
        return serializer.Deserialize<ActorMessage>(serializer.SerializeToArray(message));
    }

    public static async Task StopSiloAsync(IHost host)
    {
        await host.StopAsync();
        host.Dispose();
    }
}
