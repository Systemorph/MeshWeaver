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
            if (isDisposed)
                logger.LogWarning("[SYNC_STREAM] Not setting {StreamId} — stream is disposed", StreamId);
            else
                logger.LogDebug("[SYNC_STREAM] Skipping null value for {StreamId}", StreamId);
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
            logger.LogWarning(e, "[SYNC_STREAM] Exception setting current value for {Address}", Hub?.Address);
        }
    }

    private const string SynchronizationGate = nameof(SynchronizationGate);
    public void Update(Func<TStream?, ChangeItem<TStream>?> update, Action<Exception> exceptionCallback)
    {
        if (!TryGetActiveHub(out var hub))
            return;
        var capturedContext = CaptureCallerAccessContext(hub);
        hub.Post(
            new UpdateStreamRequest((stream, _) => Task.FromResult(update.Invoke(stream)), exceptionCallback),
            opt => capturedContext is null ? opt : opt.WithAccessContext(capturedContext));
    }

    public void Update(Func<TStream?, CancellationToken, Task<ChangeItem<TStream>?>> update,
        Action<Exception> exceptionCallback)
    {
        if (!TryGetActiveHub(out var hub))
            return;
        var capturedContext = CaptureCallerAccessContext(hub);
        hub.Post(
            new UpdateStreamRequest(update, exceptionCallback),
            opt => capturedContext is null ? opt : opt.WithAccessContext(capturedContext));
    }

    /// <summary>
    /// Captures the caller's <see cref="AccessService.Context"/> (per-request
    /// AsyncLocal) at the point <c>stream.Update</c> is invoked, so the
    /// post-pipeline can stamp the resulting <c>UpdateStreamRequest</c>
    /// delivery with the caller's identity even when the post-pipeline runs
    /// on the sync stream's internal hub thread (which has its own
    /// AsyncLocal value — typically null — and would otherwise fall back to
    /// stamping the sync hub's address as the user).
    /// <para>Returns <c>null</c> if the caller has no AccessContext set —
    /// the existing fallback behaviour (post-pipeline stamps the posting
    /// hub's address) then takes effect.</para>
    /// </summary>
    private static AccessContext? CaptureCallerAccessContext(IMessageHub hub)
    {
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        return accessService?.Context ?? accessService?.CircuitContext;
    }

    /// <summary>
    /// Resolves the synchronization hub if the stream is alive and the hub is
    /// non-null. Dead streams (constructed against a disposing parent) have
    /// no hub; calling Hub.Post would NRE. Returns false in that case so
    /// callers can no-op gracefully.
    /// </summary>
    private bool TryGetActiveHub(out IMessageHub hub)
    {
        if (isDisposed || Hub is null)
        {
            hub = null!;
            return false;
        }
        hub = Hub;
        return true;
    }

    public void OnCompleted()
    {
        if (!Store.IsDisposed)
            Store.OnCompleted();
    }

    public void OnError(Exception error)
    {
        if (!Store.IsDisposed)
        {
            // Classify the failure to avoid the log dashboard pageant where every
            // teardown / transient-timeout cascade dumps full stack traces 5×.
            // ObjectDisposedException — benign teardown; never log (Debug only).
            // TimeoutException — transient hub failure (the 30s SubscribeRequest
            //   timeout); already surfaced as a single LogWarning at the
            //   subscribe site. Don't repeat the stack trace here — Information,
            //   message-only, no exception object.
            // Everything else — real failure; LogWarning with full context.
            if (IsObjectDisposed(error))
            {
                logger.LogDebug(error,
                    "[SYNC_STREAM] OnError (disposed) for {StreamId} (Reference={Reference}, Owner={Owner})",
                    StreamId, Reference, Owner);
            }
            else if (IsTransientHubTimeout(error))
            {
                logger.LogInformation(
                    "[SYNC_STREAM] OnError (transient timeout) for {StreamId} (Reference={Reference}, Owner={Owner}): {Message}",
                    StreamId, Reference, Owner, error.Message);
            }
            else
            {
                logger.LogWarning(error,
                    "[SYNC_STREAM] OnError for {StreamId} (Reference={Reference}, Owner={Owner})",
                    StreamId, Reference, Owner);
            }
            try
            {
                Store.OnError(error);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "[SYNC_STREAM] Exception from Store.OnError propagation for {StreamId}", StreamId);
            }
            // Always fault startup and open gate, even if Store.OnError throws.
            // Dead-stream guard: Hub may be null on a stream constructed against
            // a disposing parent — there's nothing to fault or unblock there.
            if (Hub is not null)
            {
                Hub.FailStartup(error);
                Hub.OpenGate(SynchronizationGate);
            }
        }
        else
        {
            // Store already disposed — benign during teardown, never log Warning.
            logger.LogDebug("[SYNC_STREAM] OnError skipped for {StreamId} - Store is disposed", StreamId);
        }
    }

    /// <summary>
    /// True if <paramref name="error"/> (or any exception in its chain) is an
    /// <see cref="ObjectDisposedException"/> — i.e. a benign teardown artifact.
    /// </summary>
    private static bool IsObjectDisposed(Exception? error)
    {
        for (var e = error; e != null; e = e.InnerException)
            if (e is ObjectDisposedException) return true;
        return false;
    }

    /// <summary>
    /// True if <paramref name="error"/> (or any exception in its chain) is a
    /// transient hub failure — <see cref="TimeoutException"/> from the
    /// SubscribeRequest 30s wait, or a wrapped Orleans/Task cancellation. These
    /// are usually self-healing on the next subscription cycle and don't warrant
    /// a stack-trace-bearing Warning per occurrence.
    /// </summary>
    private static bool IsTransientHubTimeout(Exception? error)
    {
        for (var e = error; e != null; e = e.InnerException)
        {
            if (e is TimeoutException) return true;
            if (e is TaskCanceledException) return true;
            if (e is OperationCanceledException) return true;
        }
        return false;
    }

    public void RegisterForDisposal(IDisposable disposable)
    {
        if (isDisposed || Hub is null)
        {
            // Dead stream — no hub to register on. Dispose the registrant
            // immediately so the caller doesn't leak it. The caller's intent
            // (couple this disposable to the stream's lifetime) is satisfied
            // because the stream is already terminal.
            try { disposable.Dispose(); } catch { /* best-effort */ }
            return;
        }
        Hub.RegisterForDisposal(_ => disposable.Dispose());
    }

    public IMessageDelivery DeliverMessage(IMessageDelivery delivery)
    {
        if (isDisposed || Hub is null)
        {
            logger.LogDebug("[SYNC_STREAM] DeliverMessage skipped for {StreamId} — stream is dead/disposed", StreamId);
            return delivery.Failed("Stream is disposed");
        }
        return Hub.DeliverMessage(delivery.ForwardTo(Hub.Address));
    }


    public void OnNext(ChangeItem<TStream> value)
    {
        // Dead stream (created against a disposing parent — see ctor) has no
        // Hub. Propagate the dead state to subscribers via Store.OnCompleted
        // instead of NRE'ing on Hub.Post.
        if (isDisposed || Hub is null)
        {
            logger.LogDebug("[SYNC_STREAM] OnNext skipped for {StreamId} — stream is dead/disposed", StreamId);
            return;
        }

        try
        {
            // Self-post: sync hub posts SetCurrentRequest into itself to
            // deliver stream values. Stamp hub-self impersonation so the
            // PostPipeline AccessContext fail-closed check (sync/ + mesh)
            // doesn't drop the stream's own data delivery.
            Hub.Post(new SetCurrentRequest(value), o => o.ImpersonateAsHub(Hub.Address));
        }
        catch (Exception ex)
        {
            // Propagate to the OTHER side of the stream — subscribers see OnError
            // and can react. Without this catch, a Post failure (e.g. hub
            // mid-disposal) bubbled up as a user-unhandled exception at the
            // OnNext call site (typically inside an Rx pipeline) and the IDE
            // broke even though the upstream had a Catch.
            logger.LogWarning(ex,
                "[SYNC_STREAM] OnNext post failed for {StreamId}; forwarding to subscribers via Store.OnError",
                StreamId);
            try
            {
                if (!Store.IsDisposed)
                    Store.OnError(ex);
            }
            catch
            {
                // Store may already be terminated — best effort.
            }
        }
    }

    public virtual void RequestChange(Func<TStream?, ChangeItem<TStream>?> update, Action<Exception> exceptionCallback)
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
        this.Host = Host;
        this.Configuration = configuration?.Invoke(new StreamConfiguration<TStream>(this)) ?? new StreamConfiguration<TStream>(this);

        // Store subscriber in Properties for easy access via Get<Address>
        if (Configuration.Subscriber != null)
            Set(nameof(SubscribeRequest.Subscriber), Configuration.Subscriber);

        this.ReduceManager = ReduceManager;
        this.StreamIdentity = StreamIdentity;
        this.Reference = Reference;

        logger = Host.ServiceProvider.GetRequiredService<ILogger<SynchronizationStream<TStream>>>();

        // Disposing parent hub: don't throw — that surfaced as an unhandled
        // ObjectDisposedException at every call site (including Blazor circuit
        // teardowns where the IDE breaks on user-unhandled). The stream is
        // dead-on-arrival; mark it disposed so any Subscribe completes
        // immediately and any Update is a no-op. Callers walking the parent's
        // disposal chain will dispose this child too via the registration
        // chain — this is just defensive belt-and-braces for the late-arrival
        // race where someone calls Reduce on a parent that just started
        // disposing.
        if (Host.RunLevel > MessageHubRunLevel.Started)
        {
            logger.LogDebug(
                "[SYNC_STREAM] Parent hub {Host} disposing (RunLevel={RunLevel}); creating dead stream for {Reference}",
                Host.Address, Host.RunLevel, Reference);
            isDisposed = true;
            // Hub stays null on a dead stream — every code path that touches
            // Hub goes through TryGetActiveHub (guards isDisposed first) or
            // the explicit null check in OnNext. The null! tells the
            // compiler we accept the non-null contract gap; the runtime
            // guards enforce it.
            Hub = null!;
            Store.OnCompleted();
            return;
        }

        logger.LogDebug("Creating Synchronization Stream {StreamId} for Host {Host} and {StreamIdentity} and {Reference}", StreamId, Host.Address, StreamIdentity, Reference);

        Hub = Host.GetHostedHub(SynchronizationAddress.Create(ClientId), ConfigureSynchronizationHub);
    }

    private MessageHubConfiguration ConfigureSynchronizationHub(MessageHubConfiguration config)
    {
        config = config
            .WithTypes(
                typeof(EntityStore),
                typeof(JsonElement)
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
            ).WithHandler<GetDataResponse>((_, delivery) =>
                {
                    var response = delivery.Message;
                    if (response.Error is { } error)
                    {
                        logger.LogWarning("Stream {StreamId} subscription rejected: {Error}", StreamId, error);
                        OnError(new UnauthorizedAccessException(
                            $"Subscription to {StreamIdentity.Owner} for {Reference} failed: {error}"));
                    }
                    return delivery.Processed();
                }
            ).WithHandler<DeliveryFailure>((_, delivery) =>
                {
                    var failure = delivery.Message;
                    logger.LogWarning("Stream {StreamId} received DeliveryFailure: {Message}", StreamId, failure.Message);
                    OnError(new DeliveryFailureException(failure));
                    return delivery.Processed();
                }
            ).WithHandler<StreamErrorEvent>((_, delivery) =>
                {
                    var evt = delivery.Message;
                    logger.LogWarning("Stream {StreamId} received StreamErrorEvent: {Message}", StreamId, evt.Message);
                    OnError(new InvalidOperationException(evt.Message));
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
                    // Synchronous side-effect — Action<Exception> per the
                    // "no Task on hub-touching error paths" rule. Caller can
                    // log, push to a status subject, etc. but cannot await
                    // (which would deadlock the hub action block).
                    try { exceptionCallback.Invoke(e); }
                    catch (Exception cbEx)
                    {
                        logger.LogError(cbEx,
                            "[SYNC_STREAM] exceptionCallback threw while handling {OriginalException} on {StreamId}",
                            e.Message, StreamId);
                    }
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
            .WithInitialization(hub => Observable.FromAsync(ct => InitializeAsync(hub, ct)).Select(_ => System.Reactive.Unit.Default))
            .WithInitializationGate(SynchronizationGate, d =>
                // Init-time pass-through: messages that contribute to Current
                // being populated (initial frame from owner, error responses).
                d.Message is SetCurrentRequest or DeliveryFailure or GetDataResponse
                || d.Message is DataChangedEvent { ChangeType: ChangeType.Full }
                // UpdateStreamRequest must also pass: deferring it loses the
                // request entirely (TPL Dataflow LinkTo from deferred → main
                // doesn't re-flush the queued items in this codebase). The
                // handler reads Current synchronously at process time — if
                // Current is null (raced before SubscribeResponse), the
                // transform receives null and returns null (no-op) without
                // any harm; once Current is populated, the transform fires.
                || d.Message is UpdateStreamRequest);

        // Apply deferred initialization if configured
        if (Configuration.DeferredInitialization)
        {
            config = config.WithDeferredInitialization();
            if (Configuration.DeferredGateName != null && Configuration.DeferredGatePredicate != null)
                config = config.WithInitializationGate(Configuration.DeferredGateName, Configuration.DeferredGatePredicate);
        }

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

        if (Hub is null || Hub.Disposal is not null)
        {
            logger.LogDebug("[SYNC_STREAM] UpdateStream skipped for {StreamId} - hub is disposing/dead", StreamId);
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
                Set(currentJson);
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
            try
            {
                (currentJson, var patch) = delivery.Message.UpdateJsonElement(currentJson, hub.JsonSerializerOptions);
                var changeItem = this.ToChangeItem(Current!.Value!,
                    currentJson.Value,
                    patch,
                    delivery.Message.ChangedBy ?? ClientId);

                // PatchFunction may be null for single-object streams (e.g. MeshNodeReference).
                // Fall back to full deserialization of the patched JSON.
                changeItem ??= new ChangeItem<TStream>(
                    currentJson.Value.Deserialize<TStream>(Host.JsonSerializerOptions)!,
                    delivery.Message.ChangedBy ?? ClientId,
                    StreamId,
                    ChangeType.Patch,
                    delivery.Message.Version,
                    null);

                SetCurrent(hub, changeItem);
            }
            catch (StaleStreamStateException stale)
            {
                // Local JSON cache drifted from the owner's view. Drop our cached snapshot
                // and request a fresh Full from the owner via a new SubscribeRequest.
                logger.LogWarning(stale,
                    "[SYNC_STREAM] Stale patch for {StreamId}; requesting fresh snapshot from {Owner}.",
                    StreamId, StreamIdentity.Owner);
                Set<JsonElement?>(null);
                if (Reference is WorkspaceReference wsRef)
                {
                    Host.Post(new SubscribeRequest(StreamId, wsRef) { Subscriber = Configuration.Subscriber! },
                        o => o.WithTarget(StreamIdentity.Owner));
                }
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
    public string? Identity { get; init; }


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
        if (Hub is not null && Hub.RunLevel <= MessageHubRunLevel.Started)
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
    public record UpdateStreamRequest([property: JsonIgnore] Func<TStream?, CancellationToken, Task<ChangeItem<TStream>?>> UpdateAsync, [property: JsonIgnore] Action<Exception> ExceptionCallback);

    [PreventLogging]
    public record SetCurrentRequest(ChangeItem<TStream> Value);

}


public record StreamConfiguration<TStream>(ISynchronizationStream<TStream> Stream)
{
    internal string ClientId { get; init; } = Guid.NewGuid().AsString();
    public StreamConfiguration<TStream> WithClientId(string streamId) =>
        this with { ClientId = streamId };

    /// <summary>
    /// The address of the subscriber (client/portal) that subscribed to this stream.
    /// Used for sending messages back to the subscriber, such as NavigationRequest.
    /// </summary>
    public Address? Subscriber { get; init; }
    public StreamConfiguration<TStream> WithSubscriber(Address subscriber) =>
        this with { Subscriber = subscriber };

    internal bool NullReturn { get; init; }

    public StreamConfiguration<TStream> ReturnNullWhenNotPresent()
        => this with { NullReturn = true };

    internal Func<ISynchronizationStream<TStream>, CancellationToken, Task<TStream>>? Initialization { get; init; }


    internal Action<Exception> ExceptionCallback { get; init; } = _ => { };

    /// <summary>
    /// When true, the stream's hosted hub will not automatically post InitializeHubRequest during construction.
    /// Manual initialization is required by posting InitializeHubRequest to the stream's hub.
    /// This is useful when the stream initialization depends on properties that are set after stream construction.
    /// </summary>
    internal bool DeferredInitialization { get; init; }
    internal string? DeferredGateName { get; init; }
    internal Predicate<IMessageDelivery>? DeferredGatePredicate { get; init; }

    public StreamConfiguration<TStream> WithInitialization(Func<ISynchronizationStream<TStream>, CancellationToken, Task<TStream>> init)
        => this with { Initialization = init };

    public StreamConfiguration<TStream> WithExceptionCallback(Action<Exception> exceptionCallback)
        => this with { ExceptionCallback = exceptionCallback };

    /// <summary>
    /// Enables deferred initialization for the stream's hosted hub. When enabled, the hub will not automatically
    /// post InitializeHubRequest during construction. Manual initialization is required by posting InitializeHubRequest
    /// to the stream's hub after the stream is fully constructed.
    /// </summary>
    /// <param name="deferred">Whether to defer initialization (default: true)</param>
    /// <returns>Updated configuration</returns>
    public StreamConfiguration<TStream> WithDeferredInitialization(bool deferred = true)
        => this with { DeferredInitialization = deferred };

    /// <summary>
    /// Enables deferred initialization with a named gate. The gate is added to the stream's
    /// sub-hub and allows matching messages through while initialization is deferred.
    /// The gate is opened by calling Hub.OpenGate(gateName) when the data is ready.
    /// </summary>
    public StreamConfiguration<TStream> WithDeferredInitialization(
        string gateName, Predicate<IMessageDelivery> allowDuringInit)
        => this with { DeferredInitialization = true, DeferredGateName = gateName, DeferredGatePredicate = allowDuringInit };
}
