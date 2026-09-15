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
using Orleans.Runtime.Hosting;
using Orleans.Storage;

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

/// <summary>Implemented in Clojure (orleans.actors) and installed with <see cref="Actors.SetHost"/>.</summary>
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
/// in Clojure (EDN with a tagged literal for vars) and installed with <see cref="Actors.SetWire"/>.</summary>
public interface IClojureWire
{
    string Write(object value);
    object Read(string text);
}

/// <summary>The stored form of an actor's state, for actors that persist.</summary>
[GenerateSerializer]
public sealed class ActorState
{
    [Id(0)] public object? Value { get; set; }
}

/// <summary>The generic grain. State and behaviour belong to the Clojure runtime.</summary>
public sealed class ActorGrain : Grain, IActorGrain
{
    private readonly IPersistentState<ActorState> store;

    public ActorGrain([PersistentState("actor", Actors.StorageName)] IPersistentState<ActorState> store)
    {
        this.store = store;
    }

    /// <summary>The actor's state, owned by the Clojure runtime.</summary>
    public object? State { get; set; }

    /// <summary>Whether a state was stored for this actor (loaded before activation).</summary>
    public bool HasStoredState => store.RecordExists;

    public object? StoredState => store.State.Value;

    public async Task<bool> PersistState(object? value)
    {
        store.State.Value = value;
        await store.WriteStateAsync();
        return true;
    }

    public async Task<bool> ForgetState()
    {
        await store.ClearStateAsync();
        return true;
    }

    public string Key => this.GetPrimaryKeyString();

    public IGrainFactory Factory => GrainFactory;

    private static IActorHost Host =>
        Actors.Host ?? throw new InvalidOperationException("The Clojure actor runtime is not started: call orleans.actors/start! first.");

    public override Task OnActivateAsync(CancellationToken cancellationToken) => Host.OnActivate(this);

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken) =>
        Host.OnDeactivate(this, reason.ReasonCode.ToString());

    public async Task<ActorReply> Call(ActorMessage message)
    {
        try
        {
            return (ActorReply)(await Host.OnCall(this, message))!;
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

    private readonly List<IDisposable> timers = new();

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
                    await Host.OnInfo(this, message);
                }
                catch (Exception e)
                {
                    await Host.OnError(this, e);
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
    /// (with a fresh init) by the next message sent to it.</summary>
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
    public static bool IsClojureType(Type type) =>
        type == typeof(ClojureValue)
        || typeof(clojure.lang.IPersistentCollection).IsAssignableFrom(type)
        || typeof(clojure.lang.IFn).IsAssignableFrom(type)
        || type == typeof(clojure.lang.Keyword)
        || type == typeof(clojure.lang.Symbol)
        || type == typeof(clojure.lang.BigInt)
        || type == typeof(clojure.lang.Ratio);

    private static IClojureWire Wire =>
        Actors.Wire ?? throw new InvalidOperationException("The Clojure actor runtime is not started: call orleans.actors/start! first.");

    public bool IsSupportedType(Type type) => IsClojureType(type);

    public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, object value)
        where TBufferWriter : IBufferWriter<byte>
    {
        if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
            return;

        var bytes = Encoding.UTF8.GetBytes(Wire.Write(value));
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
        var value = Wire.Read(Encoding.UTF8.GetString(bytes));
        ReferenceCodec.RecordObject(reader.Session, value);
        return value;
    }
}

/// <summary>
/// Grain storage as EDN files, one per actor: durable across process and silo restarts,
/// shared by every silo with access to the directory. It stores the state through
/// <see cref="IClojureWire"/>, so what is on disk is what would be on the wire.
/// </summary>
public sealed class EdnFileStorage : IGrainStorage
{
    private readonly string directory;

    public EdnFileStorage(string directory) => this.directory = directory;

    /// <summary>The file for an actor: "type/id" becomes type/id.state.edn under the directory.
    /// Ids come from callers, so the path is checked to stay inside the directory.</summary>
    private string PathFor(string stateName, GrainId grainId)
    {
        var key = grainId.Key.ToString()!;
        if (key.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new ArgumentException($"Actor key '{key}' cannot be stored as a file", nameof(grainId));
        var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar) + "." + stateName + ".edn"));
        if (!path.StartsWith(root, StringComparison.Ordinal))
            throw new ArgumentException($"Actor key '{key}' escapes the storage directory", nameof(grainId));
        return path;
    }

    private static IClojureWire Wire =>
        Actors.Wire ?? throw new InvalidOperationException("The Clojure actor runtime is not started: call orleans.actors/start! first.");

    public async Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        var path = PathFor(stateName, grainId);
        if (!File.Exists(path))
        {
            grainState.RecordExists = false;
            return;
        }
        var text = await File.ReadAllTextAsync(path);
        var state = (ActorState)(object)grainState.State!;
        state.Value = Wire.Read(text);
        grainState.RecordExists = true;
        grainState.ETag = File.GetLastWriteTimeUtc(path).Ticks.ToString();
    }

    public async Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        var path = PathFor(stateName, grainId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var value = ((ActorState)(object)grainState.State!).Value;
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, value is null ? "nil" : Wire.Write(value));
        File.Move(temporary, path, overwrite: true);
        grainState.RecordExists = true;
        grainState.ETag = File.GetLastWriteTimeUtc(path).Ticks.ToString();
    }

    public Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        var path = PathFor(stateName, grainId);
        if (File.Exists(path)) File.Delete(path);
        grainState.RecordExists = false;
        grainState.ETag = null;
        return Task.CompletedTask;
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
    public static IActorHost? Host { get; private set; }

    public static IClojureWire? Wire { get; private set; }

    public static void SetHost(IActorHost host) => Host = host;

    public static void SetWire(IClojureWire wire) => Wire = wire;

    public static IActorGrain Ref(IGrainFactory factory, string key) => factory.GetGrain<IActorGrain>(key);

    /// <summary>The grain storage actors persist to: EDN files in a directory (see
    /// <see cref="EdnFileStorage"/>). A database is one AddXxxGrainStorage call instead.</summary>
    public const string StorageName = "actors";

    /// <summary>
    /// Starts an Orleans silo in this process and returns the host. Localhost clustering: the
    /// silo is the primary of its cluster unless <paramref name="primarySiloPort"/> names the
    /// silo to join. Clustering across machines is a membership provider here instead.
    /// </summary>
    public static async Task<IHost> StartSiloAsync(int siloPort, int gatewayPort, int primarySiloPort, string storageDirectory, LogLevel logLevel)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(logLevel);
        builder.Services.AddSingleton<IGeneralizedCodec, ClojureCodec>();
        builder.Services.AddSingleton<IGeneralizedCopier, ClojureCopier>();
        builder.UseOrleans(silo =>
        {
            if (primarySiloPort > 0)
                silo.UseLocalhostClustering(siloPort, gatewayPort, new IPEndPoint(IPAddress.Loopback, primarySiloPort));
            else
                silo.UseLocalhostClustering(siloPort, gatewayPort);
            silo.Services.AddGrainStorage<EdnFileStorage>(StorageName, (_, _) => new EdnFileStorage(storageDirectory));
        });
        var host = builder.Build();
        await host.StartAsync();
        return host;
    }

    public static IGrainFactory GrainFactory(IHost host) => (IGrainFactory)host.Services.GetService(typeof(IGrainFactory))!;

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
