using System.Buffers;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Storage;
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

/// <summary>What a persisting actor's state is stored as: the Clojure value, written by
/// <see cref="EdnGrainStorageSerializer"/> as EDN.</summary>
[GenerateSerializer]
public sealed class ClojureState
{
    [Id(0)] public object? Value { get; set; }
}

/// <summary>The generic grain. State and behaviour belong to the Clojure runtime; the grain keeps
/// the state slot, the timers and reminders, and its own record in an Orleans grain storage.</summary>
public sealed class ActorGrain : Grain, IActorGrain, IRemindable
{
    /// <summary>The state name of an actor's record in its grain storage.</summary>
    public const string StateName = "state";

    private readonly IActorHost host;
    private readonly List<IDisposable> timers = new();
    private readonly GrainState<ClojureState> stored = new(new ClojureState());
    private IGrainStorage? storage;

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

    // ── Persistence: the actor's own record, nobody else's ──────────

    private IGrainStorage Storage(string provider) =>
        storage ??= ServiceProvider.GetKeyedService<IGrainStorage>(provider)
                    ?? throw new InvalidOperationException(
                        $"No grain storage named \"{provider}\" is configured on this silo (actor {Key})");

    /// <summary>Reads the actor's record from the grain storage named <paramref name="provider"/>:
    /// its state, or <paramref name="none"/> when nothing is stored. The record's ETag is kept, so a
    /// write from a second activation of the same actor fails instead of overwriting.</summary>
    public async Task<object?> ReadStored(string provider, object none)
    {
        await Storage(provider).ReadStateAsync(StateName, this.GetGrainId(), stored);
        return stored.RecordExists ? stored.State.Value : none;
    }

    /// <summary>Writes the actor's record. Throws InconsistentStateException when the record
    /// changed since this activation read it.</summary>
    public Task WriteStored(string provider, object? value)
    {
        stored.State = new ClojureState { Value = value };
        return Storage(provider).WriteStateAsync(StateName, this.GetGrainId(), stored);
    }

    /// <summary>Removes the actor's record.</summary>
    public Task ClearStored(string provider) => Storage(provider).ClearStateAsync(StateName, this.GetGrainId(), stored);

    // ── Reminders: durable timers, kept by the cluster's reminder service ──

    /// <summary>Delivers a message of type <paramref name="name"/> to the actor's info handler after
    /// <paramref name="due"/>, then every <paramref name="period"/>. Unlike a timer it outlives the
    /// activation and the silo: the reminder service activates the actor wherever it is.</summary>
    public Task Remind(string name, TimeSpan due, TimeSpan period) => this.RegisterOrUpdateReminder(name, due, period);

    /// <summary>Cancels the reminder named <paramref name="name"/>, if there is one.</summary>
    public async Task Unremind(string name)
    {
        var reminder = await this.GetReminder(name);
        if (reminder is not null)
            await this.UnregisterReminder(reminder);
    }

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        try
        {
            await host.OnInfo(this, new ActorMessage(reminderName, null));
        }
        catch (Exception e)
        {
            await host.OnError(this, e);
            throw;
        }
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

/// <summary>
/// How grain storage writes an actor's state: as EDN text, the same as on the wire, so a row in the
/// database reads as the Clojure value it holds. Other types go through Orleans' own serializer.
/// </summary>
public sealed class EdnGrainStorageSerializer : IGrainStorageSerializer
{
    private readonly IClojureWire wire;
    private readonly OrleansGrainStorageSerializer fallback;

    public EdnGrainStorageSerializer(IClojureWire wire, Serializer serializer)
    {
        this.wire = wire;
        fallback = new OrleansGrainStorageSerializer(serializer);
    }

    public BinaryData Serialize<T>(T? input) =>
        input is ClojureState state
            ? BinaryData.FromString(wire.Write(state.Value!))
            : fallback.Serialize(input);

    public T? Deserialize<T>(BinaryData input) =>
        typeof(T) == typeof(ClojureState)
            ? (T)(object)new ClojureState { Value = wire.Read(input.ToString()) }
            : fallback.Deserialize<T>(input);
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
    /// <para><paramref name="configure"/>, when given, configures everything else about the silo:
    /// clustering, grain storage, reminders. Without it the silo uses localhost clustering and keeps
    /// actor states (in the "Default" grain storage) and reminders in memory.</para>
    /// </summary>
    public static async Task<IHost> StartSiloAsync(IActorHost actorHost, IClojureWire wire, int siloPort, int gatewayPort, int primarySiloPort, LogLevel logLevel,
                                                   Action<ISiloBuilder>? configure = null)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(logLevel);
        builder.Services.AddSingleton(actorHost);
        AddClojureValues(builder.Services, wire);
        builder.UseOrleans(silo =>
        {
            if (configure is not null)
            {
                silo.Configure<EndpointOptions>(o =>
                {
                    o.AdvertisedIPAddress = IPAddress.Loopback;
                    o.SiloPort = siloPort;
                    o.GatewayPort = gatewayPort;
                });
                configure(silo);
            }
            else
            {
                if (primarySiloPort > 0)
                    silo.UseLocalhostClustering(siloPort, gatewayPort, new IPEndPoint(IPAddress.Loopback, primarySiloPort));
                else
                    silo.UseLocalhostClustering(siloPort, gatewayPort);
                silo.AddMemoryGrainStorageAsDefault();
                silo.UseInMemoryReminderService();
            }
        });
        var host = builder.Build();
        await host.StartAsync();
        return host;
    }

    /// <summary>
    /// Starts an Orleans client in this process: a process that sends to actors without hosting
    /// any. <paramref name="configure"/> says how it finds the cluster (its clustering).
    /// </summary>
    public static async Task<IHost> StartClientAsync(IClojureWire wire, LogLevel logLevel, Action<IClientBuilder> configure)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(logLevel);
        AddClojureValues(builder.Services, wire);
        builder.UseOrleansClient(configure);
        var host = builder.Build();
        await host.StartAsync();
        return host;
    }

    /// <summary>Clojure values on the wire and in grain storage, through <paramref name="wire"/>.</summary>
    private static void AddClojureValues(IServiceCollection services, IClojureWire wire)
    {
        services.AddSingleton(wire);
        services.AddSingleton<IGeneralizedCodec, ClojureCodec>();
        services.AddSingleton<IGeneralizedCopier, ClojureCopier>();
        services.AddSingleton<IGrainStorageSerializer, EdnGrainStorageSerializer>();
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
