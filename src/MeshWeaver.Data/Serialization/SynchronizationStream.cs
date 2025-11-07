using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeshWeaver.Messaging;
using MeshWeaver.Reflection;
using MeshWeaver.ShortGuid;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Data.Serialization;

public record SynchronizationStream<TStream> : ISynchronizationStream<TStream>
{
    /// <summary>
    /// The stream reference, i.e. the unique identifier of the stream.
    /// </summary>
    public StreamIdentity StreamIdentity { get; }

    /// <summary>
    /// The owner of the stream. Changes are to be made as update request to the owner.
    /// </summary>
    public Address Owner => StreamIdentity.Owner;

    /// <summary>
    /// The projected reference of the stream, e.g. a collection (CollectionReference),
    /// a layout area (LayoutAreaReference), etc.
    /// </summary>
    public object Reference { get; init; }

    /// <summary>
    /// My current state deserialized as snapshot
    /// </summary>
    private ChangeItem<TStream>? current;


    /// <summary>
    /// My current state deserialized as stream
    /// </summary>
    protected readonly ReplaySubject<ChangeItem<TStream>> Store = new(1);

    object ISynchronizationStream.Reference => Reference;

    public ISynchronizationStream<TReduced>? Reduce<TReduced>(
        WorkspaceReference<TReduced> reference,
        Func<StreamConfiguration<TReduced>, StreamConfiguration<TReduced>>? config
    ) =>
        (ISynchronizationStream<TReduced>?)
            ReduceMethod
                .MakeGenericMethod(typeof(TReduced), reference.GetType())
                .Invoke(this, [reference, config]);

    private static readonly MethodInfo ReduceMethod = ReflectionHelper.GetMethodGeneric<
        SynchronizationStream<TStream>
    >(x => x.Reduce<object, WorkspaceReference<object>>(null!, null!));

    public ISynchronizationStream<TReduced> Reduce<TReduced, TReference2>(
        TReference2 reference)
        where TReference2 : WorkspaceReference =>
        Reduce<TReduced, TReference2>(reference, x => x);


    public ISynchronizationStream<TReduced>? Reduce<TReduced>(WorkspaceReference<TReduced> reference)
        => Reduce(reference, (Func<StreamConfiguration<TReduced>, StreamConfiguration<TReduced>>?)(x => x));


    public ISynchronizationStream<TReduced> Reduce<TReduced, TReference2>(
        TReference2 reference,
        Func<StreamConfiguration<TReduced>, StreamConfiguration<TReduced>> config)
        where TReference2 : WorkspaceReference =>
        ReduceManager.ReduceStream(this, reference, config) ?? throw new InvalidOperationException("Failed to create reduced stream");

    public virtual IDisposable Subscribe(IObserver<ChangeItem<TStream>> observer)
    {
        try
        {
            var subscription = Store.Synchronize().Subscribe(observer);
            logger.LogDebug("[SYNC_STREAM] Subscribe for {StreamId}, subscription created", StreamId);
            return subscription;
        }
        catch (ObjectDisposedException e)
        {
            logger.LogDebug("[SYNC_STREAM] Subscribe failed for {StreamId} - Store is disposed: {Exception}", StreamId, e.Message);
            return new AnonymousDisposable(() => { });
        }
    }

    private bool isDisposed;
    private readonly object disposeLock = new();
    private readonly ILogger<SynchronizationStream<TStream>> logger;

    public ChangeItem<TStream>? Current
    {
        get => current;
    }


    /// <summary>
    /// The actual synchronization hub
    /// </summary>
    public IMessageHub Hub { get; }

    /// <summary>
    /// The host of the synchronization stream.
    /// </summary>
    public IMessageHub Host { get; }



    public ReduceManager<TStream> ReduceManager { get; init; }

    private void SetCurrent(IMessageHub hub, ChangeItem<TStream>? value)
    {
        if (isDisposed || value == null)
        {
            logger.LogWarning("[SYNC_STREAM] Not setting {StreamId} to {Value} because the stream is disposed or value is null. IsDisposed={IsDisposed}", StreamId, value, isDisposed);
            return;
        }

        var valuesEqual = current is not null && Equals(current.Value, value.Value);


        if (current is not null && valuesEqual)
        {
            logger.LogDebug("[SYNC_STREAM] Skipping SetCurrent for {StreamId} - same version and equal values", StreamId);
            return;
        }

        current = value;
        try
        {
            logger.LogDebug("[SYNC_STREAM] Emitting OnNext for {StreamId}, Version={Version}, Store.IsDisposed={IsDisposed}, Store.HasObservers={HasObservers}",
                StreamId, value.Version, Store.IsDisposed, Store.HasObservers);
            Store.OnNext(value);
            logger.LogDebug("[SYNC_STREAM] OnNext completed for {StreamId}, opening gate", StreamId);
            hub.OpenGate(SynchronizationGate);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "[SYNC_STREAM] Exception setting current value for {Address}", Hub.Address);
        }
    }

    private const string SynchronizationGate = nameof(SynchronizationGate);
    public void Update(Func<TStream?, ChangeItem<TStream>?> update, Func<Exception, Task> exceptionCallback) =>
        Hub.Post(new UpdateStreamRequest((stream, _) => Task.FromResult(update.Invoke(stream)), exceptionCallback));

    public void Update(Func<TStream?, CancellationToken, Task<ChangeItem<TStream>?>> update,
        Func<Exception, Task> exceptionCallback) =>
        Hub.Post(new UpdateStreamRequest(update, exceptionCallback));

    public void OnCompleted()
    {
        if (!Store.IsDisposed)
            Store.OnCompleted();
    }

    public void OnError(Exception error)
    {
        if (!Store.IsDisposed)
            Store.OnError(error);
    }

    public void RegisterForDisposal(IDisposable disposable) => Hub
        .RegisterForDisposal(_ => disposable.Dispose());

    public IMessageDelivery DeliverMessage(IMessageDelivery delivery) =>
        Hub.DeliverMessage(delivery.ForwardTo(Hub.Address));


    public void OnNext(ChangeItem<TStream> value)
    {
        Hub.Post(new SetCurrentRequest(value));
    }

    public virtual void RequestChange(Func<TStream?, ChangeItem<TStream>?> update, Func<Exception, Task> exceptionCallback)
    {
        // TODO V10: Here we need to inject validations (29.07.2024, Roland Bürgi)
        Update(update, exceptionCallback);
    }

    public SynchronizationStream(
        StreamIdentity StreamIdentity,
        IMessageHub Host,
        object Reference,
        ReduceManager<TStream> ReduceManager,
        Func<StreamConfiguration<TStream>, StreamConfiguration<TStream>>? configuration)
    {
        if (Host.RunLevel > MessageHubRunLevel.Started)
            throw new ObjectDisposedException($"ParentHub {Host.Address} is disposing. Cannot create synchronization stream for {Reference}.");


        this.Host = Host;
        this.Configuration = configuration?.Invoke(new StreamConfiguration<TStream>(this)) ?? new StreamConfiguration<TStream>(this);


        this.ReduceManager = ReduceManager;
        this.StreamIdentity = StreamIdentity;
        this.Reference = Reference;

        logger = Host.ServiceProvider.GetRequiredService<ILogger<SynchronizationStream<TStream>>>();
        logger.LogDebug("Creating Synchronization Stream {StreamId} for Host {Host} and {StreamIdentity} and {Reference}", StreamId, Host.Address, StreamIdentity, Reference);

        Hub = Host.GetHostedHub(new SynchronizationAddress(ClientId), ConfigureSynchronizationHub);
    }

    private MessageHubConfiguration ConfigureSynchronizationHub(MessageHubConfiguration config)
    {
        config = config
            .WithTypes(
                typeof(EntityStore),
                typeof(JsonElement),
                typeof(SynchronizationAddress)
            )
            .WithHandler<DataChangedEvent>((hub, delivery) =>
                {
                    UpdateStream(delivery, hub);
                    return delivery.Processed();
                }
            ).WithHandler<PatchDataChangeRequest>((hub, delivery) =>
                {
                    UpdateStream(delivery, hub);
                    return delivery.Processed();
                }
            ).WithHandler<DataChangeRequest>((hub, delivery) =>
                {
                    hub.GetWorkspace().RequestChange(delivery.Message, null, delivery);
                    return delivery.Processed();
                }
            ).WithHandler<UnsubscribeRequest>((hub, delivery) =>
            {
                hub.Dispose();
                return delivery.Processed();
            }).WithHandler<UpdateStreamRequest>(async (hub, request, ct) =>
            {
                var update = request.Message.UpdateAsync;
                var exceptionCallback = request.Message.ExceptionCallback;
                try
                {
                    // Read the current state right before invoking the update function
                    // This ensures we have the latest state including any updates that occurred
                    // while previous updates were being processed
                    var currentValue = Current is null ? default : Current.Value;
                    var newChangeItem = await update.Invoke(currentValue, ct);

                    // SetCurrent will be called with the computed result
                    // The Message Hub serializes these messages, so only one UpdateStreamRequest
                    // is processed at a time per stream, preventing race conditions
                    SetCurrent(hub, newChangeItem);
                }
                catch (Exception e)
                {
                    await exceptionCallback.Invoke(e);
                }
                return request.Processed();
            }).WithHandler<SetCurrentRequest>((hub, request) =>
            {
                try
                {
                    SetCurrent(hub, request.Message.Value);
                }
                catch (Exception ex)
                {
                    throw new SynchronizationException("An error occurred during synchronization", ex);
                }
                return request.Processed();
            })
            .WithInitialization(InitializeAsync)
            .WithInitializationGate(SynchronizationGate, d => d.Message is SetCurrentRequest || d.Message is DataChangedEvent);

        // Apply deferred initialization if configured
        if (Configuration.DeferredInitialization)
            config = config.WithDeferredInitialization();

        return config;
    }

    private async Task InitializeAsync(IMessageHub hub, CancellationToken ct)
    {
        if (Configuration.Initialization is null)
        {
            // No custom initialization
            return;
        }

        var init = await Configuration.Initialization(this, ct);
        SetCurrent(hub, new ChangeItem<TStream>(init, StreamId, Host.Version));
    }


    private void UpdateStream<TChange>(IMessageDelivery<TChange> delivery, IMessageHub hub)
        where TChange : JsonChange
    {
        logger.LogDebug("[SYNC_STREAM] UpdateStream called for {StreamId}, ChangeType={ChangeType}, Version={Version}, MessageId={MessageId}",
            StreamId, delivery.Message.ChangeType, delivery.Message.Version, delivery.Id);

        if (Hub.Disposal is not null)
        {
            logger.LogWarning("[SYNC_STREAM] UpdateStream skipped for {StreamId} - hub is disposing", StreamId);
            return;
        }

        var currentJson = Get<JsonElement?>();
        if (delivery.Message.ChangeType == ChangeType.Full)
        {
            logger.LogDebug("[SYNC_STREAM] Processing Full change for {StreamId}", StreamId);
            currentJson = JsonSerializer.Deserialize<JsonElement>(delivery.Message.Change.Content);
            try
            {
                SetCurrent(hub, new ChangeItem<TStream>(
                    currentJson.Value.Deserialize<TStream>(Host.JsonSerializerOptions)!,
                    StreamId,
                    Host.Version));

            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[SYNC_STREAM] Failed to process Full change for {StreamId}", StreamId);
                SyncFailed(delivery, ex);
            }

        }
        else
        {
            logger.LogDebug("[SYNC_STREAM] Processing Patch change for {StreamId}", StreamId);
            (currentJson, var patch) = delivery.Message.UpdateJsonElement(currentJson, hub.JsonSerializerOptions);
            try
            {
                SetCurrent(hub, this.ToChangeItem(Current!.Value!,
                    currentJson.Value,
                    patch,
                    delivery.Message.ChangedBy ?? ClientId));

            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[SYNC_STREAM] Failed to process Patch change for {StreamId}", StreamId);
                SyncFailed(delivery, ex);
            }

        }
        Set(currentJson);
        logger.LogDebug("[SYNC_STREAM] UpdateStream completed for {StreamId}", StreamId);
    }

    private void SyncFailed(IMessageDelivery delivery, Exception exception)
    {
        Host.Post(new DeliveryFailure(delivery, exception.Message), o => o.ResponseFor(delivery));
    }



    public string StreamId { get; } = Guid.NewGuid().AsString();
    public string ClientId => Configuration.ClientId;


    internal StreamConfiguration<TStream> Configuration { get; }


    public void Dispose()
    {
        lock (disposeLock)
        {
            if (isDisposed)
                return;
            isDisposed = true;
        }
        Store.OnCompleted();
        Store.Dispose();
        if (Hub.RunLevel <= MessageHubRunLevel.Started)
            Hub.Dispose();
    }
    private ConcurrentDictionary<string, object?> Properties { get; } = new();
    public T? Get<T>(string key) => (T?)Properties.GetValueOrDefault(key);
    public T? Get<T>() => Get<T>(typeof(T).FullName!);
    public void Set<T>(string key, T? value) => Properties[key] = value;
    public void Set<T>(T? value) => Properties[typeof(T).FullName!] = value;

    private readonly ConcurrentDictionary<int, Task> tasks = new();
    public void BindToTask(Task task)
    {
        tasks[task.Id] = task;
        task.ContinueWith(t =>
        {
            tasks.TryRemove(task.Id, out var _);
            if (t is { IsFaulted: true, Exception: not null })
                Store.OnError(t.Exception);
        });
    }

    [PreventLogging]
    public record UpdateStreamRequest([property: JsonIgnore] Func<TStream?, CancellationToken, Task<ChangeItem<TStream>?>> UpdateAsync, [property: JsonIgnore] Func<Exception, Task> ExceptionCallback);

    [PreventLogging]
    public record SetCurrentRequest(ChangeItem<TStream> Value);

}


public record StreamConfiguration<TStream>(ISynchronizationStream<TStream> Stream)
{
    internal string ClientId { get; init; } = Guid.NewGuid().AsString();
    public StreamConfiguration<TStream> WithClientId(string streamId) =>
        this with { ClientId = streamId };

    internal bool NullReturn { get; init; }

    public StreamConfiguration<TStream> ReturnNullWhenNotPresent()
        => this with { NullReturn = true };

    internal Func<ISynchronizationStream<TStream>, CancellationToken, Task<TStream>>? Initialization { get; init; }


    internal Func<Exception, Task> ExceptionCallback { get; init; } = _ => Task.CompletedTask;

    /// <summary>
    /// When true, the stream's hosted hub will not automatically post InitializeHubRequest during construction.
    /// Manual initialization is required by posting InitializeHubRequest to the stream's hub.
    /// This is useful when the stream initialization depends on properties that are set after stream construction.
    /// </summary>
    internal bool DeferredInitialization { get; init; }

    public StreamConfiguration<TStream> WithInitialization(Func<ISynchronizationStream<TStream>, CancellationToken, Task<TStream>> init)
        => this with { Initialization = init };

    public StreamConfiguration<TStream> WithExceptionCallback(Func<Exception, Task> exceptionCallback)
        => this with { ExceptionCallback = exceptionCallback };

    public StreamConfiguration<TStream> WithExceptionCallback(Action<Exception> exceptionCallback)
        => this with { ExceptionCallback = ex => { exceptionCallback(ex); return Task.CompletedTask; } };

    /// <summary>
    /// Enables deferred initialization for the stream's hosted hub. When enabled, the hub will not automatically
    /// post InitializeHubRequest during construction. Manual initialization is required by posting InitializeHubRequest
    /// to the stream's hub after the stream is fully constructed.
    /// </summary>
    /// <param name="deferred">Whether to defer initialization (default: true)</param>
    /// <returns>Updated configuration</returns>
    public StreamConfiguration<TStream> WithDeferredInitialization(bool deferred = true)
        => this with { DeferredInitialization = deferred };
}
