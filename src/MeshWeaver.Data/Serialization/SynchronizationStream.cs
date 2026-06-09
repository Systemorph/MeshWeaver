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

        // 🚨 Dedup PATCHES ONLY. A FULL push always applies — it is the owner re-asserting
        // its complete authoritative state (initial snapshot, SetFull overwrite, rollback /
        // resync), so it must land even when value-equal to what THIS stream currently holds:
        // a downstream mirror that optimistically diverged re-converges only if the Full is
        // applied + re-emitted here. Suppressing a value-equal Full is what swallowed the
        // rollback. Symmetric with the monotonicity guard in UpdateStream (Fulls bypass version).
        if (current is not null && valuesEqual && value.ChangeType != ChangeType.Full)
        {
            logger.LogDebug("[SYNC_STREAM] Skipping SetCurrent for {StreamId} - same value (patch)", StreamId);
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
        {
            SignalDisposedToProducer(exceptionCallback);
            return;
        }
        var capturedContext = CaptureCallerAccessContext(hub);
        hub.Post(
            new UpdateStreamRequest((stream, _) => Task.FromResult(update.Invoke(stream)), exceptionCallback),
            opt => capturedContext is null ? opt : opt.WithAccessContext(capturedContext));
    }

    public void Update(Func<TStream?, CancellationToken, Task<ChangeItem<TStream>?>> update,
        Action<Exception> exceptionCallback)
    {
        if (!TryGetActiveHub(out var hub))
        {
            SignalDisposedToProducer(exceptionCallback);
            return;
        }
        var capturedContext = CaptureCallerAccessContext(hub);
        hub.Post(
            new UpdateStreamRequest(update, exceptionCallback),
            opt => capturedContext is null ? opt : opt.WithAccessContext(capturedContext));
    }

    /// <summary>
    /// Incoming write to a dead/disposed stream: error back to the PRODUCER via its
    /// <paramref name="exceptionCallback"/> so it tears down its own source (a FileSystemWatcher,
    /// a remote subscription, a timer) instead of pushing into the void — "incoming streams start
    /// erroring" on teardown. The signal is an <see cref="ObjectDisposedException"/>, the benign
    /// teardown marker the rest of the stream already classifies as Debug-only. The callback itself
    /// is guarded: a producer that throws from its handler must not escape onto a background thread.
    /// </summary>
    private void SignalDisposedToProducer(Action<Exception> exceptionCallback)
    {
        try
        {
            exceptionCallback(new ObjectDisposedException(
                nameof(SynchronizationStream<TStream>),
                $"Stream {StreamId} is disposed; the incoming update was rejected — stop the source."));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex,
                "[SYNC_STREAM] producer exceptionCallback threw while signalling disposed for {StreamId}", StreamId);
        }
    }

    /// <summary>
    /// 🚨 Canonical VALUE-based write. The caller supplies a pure value transform
    /// only — the sync stream builds the <see cref="ChangeItem{TStream}"/> itself,
    /// CONSISTENTLY for every write, so callers can never get the error-prone bits
    /// (per-entity <c>Updates</c>, <c>ChangeType</c>, and especially
    /// <c>Version</c>) wrong:
    /// <list type="bullet">
    ///   <item><description><b>Updates</b> are derived through the registered
    ///     PatchFunction (e.g. PatchMeshNode) so the owner's write-back
    ///     (<c>ToDataChangeRequest</c>) gets a well-formed per-entity delta —
    ///     a hand-rolled EntityUpdate from a caller silently failed to persist.</description></item>
    ///   <item><description><b>Version is set by the OWNING hub only.</b> The
    ///     owner stamps its monotonic <c>Hub.Version</c>; a subscriber carries
    ///     the BASE version it last observed (<c>Current.Version</c>) so the owner
    ///     can fast-forward (base == current) or merge (base &lt; current). No sync
    ///     hub mints its own version — DateTime is not a universal clock, the
    ///     owner's Version is the one reliable ordering.</description></item>
    /// </list>
    /// A no-op transform (returns the same value or null) is dropped.
    /// </summary>
    public void Update(Func<TStream?, TStream?> valueUpdate, Action<Exception> exceptionCallback)
        => Update(current =>
        {
            var updated = valueUpdate(current);
            if (updated is null) return null;
            if (current is not null && Equals(current, updated)) return null;
            return BuildChangeItem(current, updated);
        }, exceptionCallback);

    /// <inheritdoc cref="Update(System.Func{TStream,TStream},System.Action{System.Exception})"/>
    public void Update(Func<TStream?, TStream?> valueUpdate)
        => Update(valueUpdate, _ => { });

    /// <summary>
    /// 🚨 Full-replace write (OVERWRITE) — see
    /// <see cref="ISynchronizationStream{TStream}.SetFull(System.Func{TStream,TStream},System.Action{System.Exception})"/>.
    /// Identical to <see cref="Update(System.Func{TStream,TStream},System.Action{System.Exception})"/>
    /// except the change is emitted as <see cref="ChangeType.Full"/> (complete authoritative state)
    /// instead of a field-level Patch, so it lands on every mirror unconditionally and re-asserts
    /// truth. The per-entity <c>Updates</c> are still populated so the owner's write-back persists
    /// it. Unlike Update, an unchanged value is NOT short-circuited here — a Full is an explicit
    /// re-assertion; an identical-JSON no-op is dropped later by the change-feed (<c>ToDataChanged</c>).
    /// </summary>
    public void SetFull(Func<TStream?, TStream?> valueUpdate, Action<Exception> exceptionCallback)
        => Update(current =>
        {
            var updated = valueUpdate(current);
            if (updated is null) return null;
            return BuildFullChangeItem(current, updated);
        }, exceptionCallback);

    /// <inheritdoc cref="SetFull(System.Func{TStream,TStream},System.Action{System.Exception})"/>
    public void SetFull(Func<TStream?, TStream?> valueUpdate)
        => SetFull(valueUpdate, _ => { });

    /// <summary>
    /// Builds the <see cref="ChangeItem{TStream}"/> for a value transform — the
    /// single place that knows how to fill Updates + ChangeType + Version. See
    /// <see cref="Update(System.Func{TStream,TStream},System.Action{System.Exception})"/>.
    /// </summary>
    private ChangeItem<TStream> BuildChangeItem(TStream? current, TStream updated)
    {
        // 🚨 ChangedBy is the stream-echo-suppression key — the identity of the STREAM that
        // originated the change — and it must MATCH the value the echo-suppression filters
        // compare against, which is `reduced.ClientId` (JsonSynchronizationStream's
        // `reduced.ClientId.Equals(c.ChangedBy)` on the client→owner path, and
        // `!reduced.ClientId.Equals(c.ChangedBy)` on the owner→subscriber path). So ChangedBy
        // is ALWAYS ClientId — the stream's stable identity (what `WithClientId(streamId)`
        // sets; "stream id" in our vocabulary). It is NEVER the per-instance `StreamId`
        // property (a fresh Guid that never equals any ClientId): a StreamId here makes the
        // client→owner filter `ClientId.Equals(StreamId)` permanently false, so a client's
        // `stream.Update` write never becomes a PatchDataChangeRequest and silently drops.
        // The AccessContext (RLS / LastModifiedBy auditing) is ORTHOGONAL and must not leak
        // into it: deriving ChangedBy from `CaptureCallerAccessContext()?.ObjectId ?? ClientId`
        // collapses to "" when ObjectId is "" (not null, so `?? ClientId` doesn't fire), and
        // an empty ChangedBy breaks both filters. ClientId is a non-empty Guid by construction.
        var changedBy = ClientId;
        // 🚨 ONLY the owning hub sets Version. Subscriber carries the base it read.
        var version = Owner.Equals(Host.Address) ? Hub.Version : (Current?.Version ?? 0L);

        if (current is not null)
        {
            var updatedJson = JsonSerializer.SerializeToElement(updated, Hub.JsonSerializerOptions);
            // 1. PatchFunction (e.g. PatchMeshNode) derives the per-entity Updates
            //    from current→updated. Registered on the OWNER's reduce config; a
            //    lightweight subscriber may not have it, so this can be null.
            var ci = this.ToChangeItem(current, updatedJson, null, changedBy);
            if (ci is not null)
                return ci with { Version = version };

            // 2. No PatchFunction (subscriber side). Build the per-entity delta
            //    for a single-entity reduced stream directly from the type
            //    registry — collection + key — so the owner's write-back
            //    (ToDataChangeRequest) gets a well-formed Update. Without this the
            //    change shipped as a Full with empty Updates and the write-back's
            //    `Updates.Any()` filter dropped it, so the write never persisted.
            var typeRegistry = Hub.ServiceProvider.GetService<MeshWeaver.Domain.ITypeRegistry>();
            if (typeRegistry is not null
                && typeRegistry.TryGetCollectionName(typeof(TStream), out var collection)
                && !string.IsNullOrEmpty(collection))
            {
                var keyFn = typeRegistry.GetKeyFunction(typeof(TStream));
                var key = keyFn?.Function(updated!) ?? (object)updated!;
                return new ChangeItem<TStream>(updated, changedBy, StreamId, ChangeType.Patch, version,
                    [new EntityUpdate(collection!, key, updated) { OldValue = current }]);
            }
        }
        return new ChangeItem<TStream>(updated, changedBy, StreamId, ChangeType.Full, version, null);
    }

    /// <summary>
    /// Builds a <see cref="ChangeType.Full"/> <see cref="ChangeItem{TStream}"/> for an overwrite.
    /// Same per-entity <c>Updates</c> derivation as <see cref="BuildChangeItem"/>'s subscriber
    /// fallback (type-registry collection + key) so the owner's write-back
    /// (<c>ToDataChangeRequest</c>, which keys off <c>Updates</c>) persists the overwrite — the
    /// ONLY difference from <see cref="BuildChangeItem"/> is that <c>ChangeType</c> is forced to
    /// <see cref="ChangeType.Full"/>, which makes the change land on every mirror unconditionally
    /// (the monotonicity guard bypasses version for Fulls). See
    /// <see cref="SetFull(System.Func{TStream,TStream},System.Action{System.Exception})"/>.
    /// </summary>
    private ChangeItem<TStream> BuildFullChangeItem(TStream? current, TStream updated)
    {
        // ChangedBy = ClientId always (never empty; matches the echo-suppression filters,
        // never the per-instance StreamId). AccessContext is orthogonal. See BuildChangeItem.
        var changedBy = ClientId;
        // 🚨 ONLY the owning hub sets Version. Subscriber carries the base it read.
        var version = Owner.Equals(Host.Address) ? Hub.Version : (Current?.Version ?? 0L);

        var typeRegistry = Hub.ServiceProvider.GetService<MeshWeaver.Domain.ITypeRegistry>();
        if (typeRegistry is not null
            && typeRegistry.TryGetCollectionName(typeof(TStream), out var collection)
            && !string.IsNullOrEmpty(collection))
        {
            var keyFn = typeRegistry.GetKeyFunction(typeof(TStream));
            var key = keyFn?.Function(updated!) ?? (object)updated!;
            return new ChangeItem<TStream>(updated, changedBy, StreamId, ChangeType.Full, version,
                [new EntityUpdate(collection!, key, updated) { OldValue = current }]);
        }
        // No collection mapping → Full with no Updates: lands on mirrors but won't persist (the
        // write-back's Updates.Any() filter drops it). MeshNode always has a mapping, so the
        // overwrite path that matters is covered.
        return new ChangeItem<TStream>(updated, changedBy, StreamId, ChangeType.Full, version, null);
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
        // A late background trigger can reach stream.Update while the hub is tearing down —
        // e.g. a FileSystemWatcher Changed event racing ContentCollection disposal. By then the
        // hub's Autofac LifetimeScope may already be disposed, and GetService THROWS
        // ObjectDisposedException on a disposed scope. There is no caller AccessContext to capture
        // during teardown, so fall back to null (the method's documented no-context path; the
        // resulting post is then dropped by the hub's teardown guard). Surfacing it instead would
        // escape onto the watcher's threadpool thread unobserved → process-fatal.
        try
        {
            var accessService = hub.ServiceProvider.GetService<AccessService>();
            return accessService?.Context ?? accessService?.CircuitContext;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
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
            // SetCurrentRequest is sync-stream protocol — receiver does not
            // gate on AccessControl (HandleSetCurrent at
            // SynchronizationStream.cs:496). The record is marked
            // [SystemMessage] so the PostPipeline accepts a null AccessContext
            // without warning. User-data flows through this method preserve
            // user identity via the standard PostPipeline path: if AsyncLocal
            // has a user (e.g. when a Blazor data-binding push reaches OnNext
            // through a CarryAccessContext-wrapped chain), that user rides
            // delivery.AccessContext naturally. No ImpersonateAsHub stamping
            // here — hub addresses were polluting CreatedBy on user-driven
            // writes via the AsyncLocal leak (fixed 2026-05-22).
            Hub.Post(new SetCurrentRequest(value));
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
            .WithInitialization(hub => Initialize(hub).Select(_ => System.Reactive.Unit.Default))
            .WithInitializationGate(SynchronizationGate, d =>
                // Init-time pass-through: messages that contribute to Current
                // being populated (initial frame from owner, error responses).
                d.Message is SetCurrentRequest or DeliveryFailure or GetDataResponse
                // 🚨 Pass BOTH Full AND Patch DataChangedEvents through during init.
                // Gated (deferred) messages are LOST — TPL Dataflow's LinkTo from the
                // deferred block to main doesn't re-flush queued items in this codebase
                // (the same reason UpdateStreamRequest must pass, below). A producer
                // that updates its state in the window between the client's
                // SubscribeRequest and init completion ships that update as a PATCH;
                // deferring it drops it permanently and the client hangs forever on the
                // stale initial Full — the LinkedIn / ColdStart / Resubmit / HungSubThread
                // "observable never emits" CI races. A Patch that races ahead of the base
                // Full (Current still null) is handled by UpdateStream: it requests a
                // fresh Full instead of applying onto a missing snapshot.
                || d.Message is DataChangedEvent
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

    /// <summary>
    /// Drives initialization as a single reactive pipeline (no <c>await</c> on the
    /// hub-init path). Three cases:
    /// <list type="bullet">
    /// <item>Observable init configured — subscribe to it; EACH emission becomes a
    /// <c>SetCurrent</c> (a Full). This is the layout-area render path: a generator
    /// that emits its content over time flows through these emissions and is never
    /// dropped by the init window.</item>
    /// <item>Task init configured — bridge the Task at the boundary via
    /// <see cref="Observable.FromAsync{TResult}(Func{CancellationToken, Task{TResult}})"/>
    /// and set the single result as current (preserves every existing
    /// Task-based caller).</item>
    /// <item>Neither — complete immediately with no current value set.</item>
    /// </list>
    /// The returned observable signals (via its first <c>OnNext</c>) that the initial
    /// value has been produced so the hub-init gate opens; the underlying subscription
    /// stays alive (owned by the stream) for any later emissions.
    /// </summary>
    private IObservable<System.Reactive.Unit> Initialize(IMessageHub hub)
    {
        if (Configuration.ObservableInitialization is not null)
        {
            return Observable.Create<System.Reactive.Unit>(observer =>
            {
                // The init gate opens on the FIRST emission (the hub-init consumer is
                // FirstAsync), then disposes ITS subscription to this Create. The inner
                // generator subscription must outlive that — a layout area whose function
                // is a long-lived IObservable re-emits over the area's whole lifetime —
                // so we own it via RegisterForDisposal (dies with the stream) and hand
                // FirstAsync a no-op disposable. Disposing the Create subscription must
                // NOT tear down the live generator.
                var subscription = Configuration.ObservableInitialization(this).Subscribe(
                    value =>
                    {
                        SetCurrent(hub, new ChangeItem<TStream>(value, StreamId, Host.Version));
                        observer.OnNext(System.Reactive.Unit.Default);
                    },
                    observer.OnError,
                    observer.OnCompleted);
                RegisterForDisposal(subscription);
                return System.Reactive.Disposables.Disposable.Empty;
            });
        }

        if (Configuration.Initialization is not null)
        {
            return Observable
                .FromAsync(ct => Configuration.Initialization(this, ct))
                .Select(init =>
                {
                    SetCurrent(hub, new ChangeItem<TStream>(init, StreamId, Host.Version));
                    return System.Reactive.Unit.Default;
                });
        }

        // No custom initialization.
        return Observable.Return(System.Reactive.Unit.Default);
    }


    private void UpdateStream<TChange>(IMessageDelivery<TChange> delivery, IMessageHub hub)
        where TChange : JsonChange
    {
        logger.LogDebug("[SYNC_STREAM] UpdateStream called for {StreamId}, ChangeType={ChangeType}, Version={Version}, MessageId={MessageId}",
            StreamId, delivery.Message.ChangeType, delivery.Message.Version, delivery.Id);

        if (Hub is null || Hub.IsDisposing)
        {
            logger.LogDebug("[SYNC_STREAM] UpdateStream skipped for {StreamId} - hub is disposing/dead", StreamId);
            return;
        }

        // 🚨 Monotonicity guard — PATCHES ONLY. A patch is a delta computed
        // against a specific base version; a reordered OLDER patch would corrupt
        // the mirror, so we drop it. A FULL is different: it is the owner's
        // COMPLETE authoritative state and is ALWAYS applied, no matter the
        // version. A Full is normally a ROLL-BACK — the owner re-asserting truth
        // (e.g. after it REJECTED a client's optimistic change) — and it must
        // land even though the client optimistically bumped its Current to a
        // higher version. Letting Fulls through unconditionally is what makes the
        // reject→rollback undo work.
        if (delivery.Message.ChangeType != ChangeType.Full
            && Current is not null && delivery.Message.Version < Current.Version)
        {
            logger.LogDebug(
                "[SYNC_STREAM] Dropping stale patch for {StreamId}: incoming v{In} < current v{Cur}",
                StreamId, delivery.Message.Version, Current.Version);
            return;
        }

        var currentJson = Get<JsonElement?>();
        if (delivery.Message.ChangeType == ChangeType.Full)
        {
            logger.LogDebug("[SYNC_STREAM] Processing Full change for {StreamId}", StreamId);
            currentJson = JsonSerializer.Deserialize<JsonElement>(delivery.Message.Change.Content);
            try
            {
                // 🚨 Adopt the OWNER's Version (not local Host.Version) so the
                // monotonicity guard above compares apples-to-apples and a later
                // client write records the owner-version it was based on.
                SetCurrent(hub, new ChangeItem<TStream>(
                    currentJson.Value.Deserialize<TStream>(Host.JsonSerializerOptions)!,
                    StreamId,
                    delivery.Message.Version));
                Set(currentJson);
                // A Full re-established Current — any pending resync is satisfied;
                // allow a future Patch-before-Full gap to resubscribe again.
                _resyncInFlight = false;
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
            // A Patch can race ahead of the initial Full during the subscribe
            // handshake — the producer updated its state in the window between our
            // SubscribeRequest and init completing (the init gate now passes Patches
            // through, see Configure). With no base snapshot to apply onto, ask the
            // owner for a fresh Full instead of dereferencing the null Current. This
            // is the fix for the "observable never emits" CI races (LinkedIn,
            // ColdStart, Resubmit, HungSubThread).
            if (Current is null || currentJson is null)
            {
                // A Patch raced ahead of the base Full during the subscribe handshake.
                // We can't apply it onto a missing snapshot — and we must NOT just drop
                // it and trust the owner's Full to carry the change: that Full may have
                // been computed BEFORE this change (the producer updated in the
                // subscribe→init window, or the Full/Patch reordered on the wire), so
                // the change would be LOST and the consumer would sit on stale state
                // forever — the "stream never emits" CI deadlock (CreateThread,
                // RapidSubmits, TodoDataChangeWorkflow query waits). Request a fresh
                // Full so we get the CURRENT state including this change. Flood-safe:
                // RequestFreshSnapshot is gated by _resyncInFlight — exactly ONE
                // resubscribe per gap, cleared when a Full re-establishes Current.
                logger.LogDebug(
                    "[SYNC_STREAM] Patch before base Full for {StreamId}; requesting fresh snapshot", StreamId);
                RequestFreshSnapshot();
                return;
            }
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

                // 🚨 Adopt the OWNER's Version (the PatchFunction stamps the local
                // stream.Hub.Version). Keeps Current.Version on the owner's clock so
                // the monotonicity guard is consistent across Full and Patch.
                changeItem = changeItem with { Version = delivery.Message.Version };

                SetCurrent(hub, changeItem);
            }
            catch (StaleStreamStateException stale)
            {
                // Local JSON cache drifted from the owner's view (concurrent updates
                // whose Updates were computed against an older snapshot). Drop our
                // snapshot and request a fresh Full from the owner.
                logger.LogWarning(stale,
                    "[SYNC_STREAM] Stale patch for {StreamId}; requesting fresh snapshot from {Owner}.",
                    StreamId, StreamIdentity.Owner);
                RequestFreshSnapshot();
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

    /// <summary>
    /// Drop the (absent or stale) cached JSON snapshot and ask the owner for a
    /// fresh <see cref="ChangeType.Full"/> via a new <see cref="SubscribeRequest"/>.
    /// Called both when a Patch can't be applied onto the current snapshot
    /// (<see cref="StaleStreamStateException"/>) and when a Patch races ahead of
    /// the initial Full during the subscribe handshake (Current still null).
    /// The resubscribe is INFRASTRUCTURE (cache refresh) — stamped System so the
    /// owner's RLS doesn't deny on whatever ambient identity is set on the
    /// emission thread (often a <c>sync/&lt;id&gt;</c> hub address); user-level
    /// access enforcement happens at the consumer layer, not this seam.
    /// </summary>
    // Guards against a resync STORM. RequestFreshSnapshot nulls the cached JSON,
    // so every subsequent Patch (until the fresh Full lands) re-enters the
    // "Patch before base Full" path. Without this gate each of those Patches would
    // post another SubscribeRequest, flooding the owner's action block and starving
    // real requests (the TodoDataChangeWorkflow UpdateNodeRequest leak). One
    // resubscribe per gap; cleared when a Full re-establishes Current.
    private bool _resyncInFlight;

    private void RequestFreshSnapshot()
    {
        if (_resyncInFlight)
            return;
        _resyncInFlight = true;
        Set<JsonElement?>(null);
        if (Reference is WorkspaceReference wsRef)
        {
            var accessService = Host.ServiceProvider
                .GetService(typeof(AccessService)) as AccessService;
            using (accessService?.ImpersonateAsSystem())
            {
                Host.Post(new SubscribeRequest(StreamId, wsRef) { Subscriber = Configuration.Subscriber! },
                    o => o.WithTarget(StreamIdentity.Owner));
            }
        }
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

    /// <summary>
    /// Synchronisation-protocol message that propagates a state change to the
    /// owner. Not a user-write request — the receiver does NOT gate on
    /// AccessControl (see <c>StreamHandlers.HandleSetCurrent</c>). Marked
    /// <see cref="SystemMessageAttribute"/> so the PostPipeline doesn't warn
    /// when AsyncLocal AccessContext is empty (typical on Rx scheduler hops
    /// where the stream's <c>OnNext</c> fires). User-data carrying paths
    /// preserve identity via the standard PostPipeline + CarryAccessContext
    /// wrap — no ImpersonateAsHub stamping in this protocol layer.
    /// </summary>
    [PreventLogging]
    [SystemMessage]
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

    /// <summary>
    /// Observable initialization. Each emitted value is set as the stream's current
    /// value (<c>SetCurrent</c>, as a <c>Full</c>), so a renderer/generator that emits
    /// its content over time (e.g. a layout area whose function returns an
    /// <see cref="IObservable{T}"/>) flows through the init subscription's own
    /// emissions — those are never dropped by the init window the way a
    /// <c>Stream.Update</c> issued during init would be. Each emission is a complete
    /// snapshot (the same shape as the Task-based init's single <c>SetCurrent</c>),
    /// which reliably delivers a freshly-built control tree — including a container's
    /// nested sub-areas whose keys contain <c>/</c> — to the client's per-area control
    /// streams (a Full carries no per-area Updates, so consumers re-evaluate against
    /// it; see <c>LayoutExtensions.GetStream</c>). Mutually exclusive with
    /// <see cref="Initialization"/> (the Task-based path); when set, the stream
    /// subscribes to it synchronously (no <c>await</c>).
    /// </summary>
    internal Func<ISynchronizationStream<TStream>, IObservable<TStream>>? ObservableInitialization { get; init; }


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

    /// <summary>
    /// Observable initialization. Each emitted value is set as the stream's current
    /// value (<c>SetCurrent</c>, as a <c>Full</c>). Use this when the stream's content
    /// arrives over time (the layout-area render path) so the emissions are delivered
    /// as the init subscription's own <c>SetCurrent</c> calls instead of being issued
    /// as <c>Stream.Update</c> requests that the init window drops. Each emission is a
    /// complete snapshot. The subscription is registered for disposal with the stream.
    /// </summary>
    public StreamConfiguration<TStream> WithInitialization(Func<ISynchronizationStream<TStream>, IObservable<TStream>> init)
        => this with { ObservableInitialization = init };

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
