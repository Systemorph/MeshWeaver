using System.Collections.Concurrent;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeshWeaver.Messaging;
using MeshWeaver.Reflection;
using MeshWeaver.ShortGuid;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeshWeaver.Data.Serialization;

/// <summary>
/// Concrete synchronization stream: a hub-backed, reactive store of a single state value of type
/// <typeparamref name="TStream"/> that is kept in sync with its owner via change items
/// (full snapshots and patches). Subscribers observe the latest state, writers mutate it through
/// <see cref="Update(System.Func{TStream,TStream},System.Action{System.Exception})"/>/<c>SetFull</c>,
/// and reduced/derived streams are produced through the <see cref="ReduceManager"/>.
/// </summary>
/// <typeparam name="TStream">Type of the state carried by the stream.</typeparam>
public record SynchronizationStream<TStream> : ISynchronizationStream<TStream>, IStreamLivenessSource
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

    /// <summary>
    /// Derives a reduced stream of <typeparamref name="TReduced"/> from this stream for the given
    /// reference, dispatching to the strongly-typed reducer via reflection on the reference's runtime type.
    /// </summary>
    /// <typeparam name="TReduced">Type of the reduced state.</typeparam>
    /// <param name="reference">The reference selecting the reduced slice.</param>
    /// <param name="config">Optional configuration for the reduced stream.</param>
    /// <returns>The reduced stream, or <c>null</c> if it cannot be produced.</returns>
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

    /// <summary>
    /// Derives a reduced stream of <typeparamref name="TReduced"/> for the given reference using the
    /// default (identity) configuration.
    /// </summary>
    /// <typeparam name="TReduced">Type of the reduced state.</typeparam>
    /// <typeparam name="TReference2">The reference type selecting the reduced slice.</typeparam>
    /// <param name="reference">The reference selecting the reduced slice.</param>
    /// <returns>The reduced stream.</returns>
    public ISynchronizationStream<TReduced> Reduce<TReduced, TReference2>(
        TReference2 reference)
        where TReference2 : WorkspaceReference =>
        Reduce<TReduced, TReference2>(reference, x => x);


    /// <summary>
    /// Derives a reduced stream of <typeparamref name="TReduced"/> for the given reference using the
    /// default (identity) configuration.
    /// </summary>
    /// <typeparam name="TReduced">Type of the reduced state.</typeparam>
    /// <param name="reference">The reference selecting the reduced slice.</param>
    /// <returns>The reduced stream, or <c>null</c> if it cannot be produced.</returns>
    public ISynchronizationStream<TReduced>? Reduce<TReduced>(WorkspaceReference<TReduced> reference)
        => Reduce(reference, (Func<StreamConfiguration<TReduced>, StreamConfiguration<TReduced>>?)(x => x));

    // 🚨 Intermediate reduced streams are CACHED, for the same reason Workspace.GetStream caches
    // local reduced streams (#1345) and _remoteStreamCache caches mirrors: a reduce is neither free
    // nor garbage-collectable.
    //
    // CreateReducedStream constructs a SynchronizationStream — hence a hosted `sync/{id}` sub-hub
    // with its own Autofac scope, TypeRegistry and JsonSerializerOptions (~140 KB) — and registers
    // it for disposal ON THIS STREAM. So when THIS stream is hub-lifetime (a data source's primary
    // EntityStore), every uncached reduce is a hub that survives until the hub dies. MeshDataSource's
    // own-node factory paid exactly that once per inbound SubscribeRequest, which is the residual
    // measured in Systemorph/MeshWeaver#1324 after #1415 closed the eviction-parking retainer.
    //
    // Keyed by reference only: a shared reduce carries no caller-specific configuration by
    // construction (that is what makes it shareable — see ISynchronizationStream.ReduceShared).
    private readonly ConcurrentDictionary<WorkspaceReference, Lazy<ISynchronizationStream>> sharedReduceCache = new();

    /// <inheritdoc />
    public ISynchronizationStream<TReduced>? ReduceShared<TReduced>(WorkspaceReference<TReduced> reference)
    {
        // 🚨 A DISPOSED PARENT IS NEVER SERVED FROM CACHE — sharing must not outlive the thing being
        // shared FROM. The cached child's own sub-hub is a sibling under `Host`, not a child of this
        // stream's hub, so it stays alive when this stream is disposed: a liveness check on the child
        // alone happily hands back a mirror of a dead source. Reads then bind to a stream that will
        // never emit and never complete, so a reader gets neither an answer nor the NACK that
        // `DataExtensions.HandleGetDataRequest`'s disposal arm owes it — it hangs for its whole
        // budget (SilentReadNackTest, both arms). Falling through to the plain reduce keeps the
        // behaviour a disposed parent has always had.
        if (isDisposed)
            return Reduce(reference, x => x);

        while (true)
        {
            var mine = new Lazy<ISynchronizationStream>(
                () => Reduce(reference, x => x)
                      ?? throw new InvalidOperationException(
                          $"Cannot reduce stream {StreamId} to {reference}"),
                LazyThreadSafetyMode.ExecutionAndPublication);
            var lazy = sharedReduceCache.GetOrAdd(reference, mine);

            ISynchronizationStream reduced;
            try
            {
                reduced = lazy.Value;
            }
            catch
            {
                // A Lazy in ExecutionAndPublication mode CACHES the exception, so one transient
                // failure (e.g. HubDisposingException from a hub winding down) would otherwise
                // poison this reference for the parent's whole life. Drop the faulted entry — only
                // if it is still ours — and let the caller see the original fault.
                Remove(reference, lazy);
                throw;
            }

            // Same liveness contract as Workspace.GetStream — literally the same predicate, so the
            // two cannot drift apart again (they did: #1455).
            if (StreamLiveness.IsUsable(reduced))
                return (ISynchronizationStream<TReduced>)reduced;

            Remove(reference, lazy);

            // 🚨 A FRESH reduce that is ALREADY unusable means the SOURCE is dead — disposed, or
            // (since #2387) terminally faulted, which the reduce chain propagates into every child
            // it can ever build. Re-reducing is not a repair and repeating it is an unbounded
            // spin, one SynchronizationStream and `sync/{id}` sub-hub per turn. Hand this one
            // back: it is exactly what the plain uncached reduce produces, and its store already
            // carries the terminal so a reader gets an END rather than a stale replay followed by
            // eternal silence. The same guard Workspace.GetStream carries for its own cache.
            if (ReferenceEquals(lazy, mine))
                return (ISynchronizationStream<TReduced>)reduced;

            // We lost the add race to another caller's entry and it was dead: drop it and let the
            // next turn install ours. Every turn removes one entry, so this cannot spin.
        }

        void Remove(WorkspaceReference key, Lazy<ISynchronizationStream> entry) =>
            ((ICollection<KeyValuePair<WorkspaceReference, Lazy<ISynchronizationStream>>>)sharedReduceCache)
                .Remove(new KeyValuePair<WorkspaceReference, Lazy<ISynchronizationStream>>(key, entry));
    }


    /// <summary>
    /// Derives a reduced stream of <typeparamref name="TReduced"/> for the given reference, applying the
    /// supplied configuration.
    /// </summary>
    /// <typeparam name="TReduced">Type of the reduced state.</typeparam>
    /// <typeparam name="TReference2">The reference type selecting the reduced slice.</typeparam>
    /// <param name="reference">The reference selecting the reduced slice.</param>
    /// <param name="config">Configures the reduced stream.</param>
    /// <returns>The reduced stream.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the reduced stream cannot be created.</exception>
    public ISynchronizationStream<TReduced> Reduce<TReduced, TReference2>(
        TReference2 reference,
        Func<StreamConfiguration<TReduced>, StreamConfiguration<TReduced>> config)
        where TReference2 : WorkspaceReference =>
        ReduceManager.ReduceStream(this, reference, config) ?? throw new InvalidOperationException("Failed to create reduced stream");

    /// <summary>
    /// Subscribes an observer to the stream's change items, replaying the most recent change immediately.
    /// </summary>
    /// <param name="observer">The observer to receive change items.</param>
    /// <returns>A disposable that ends the subscription. On a DISPOSED stream the store is
    /// completed (never disposed — #1170/#1171), so a late subscriber receives any replayed
    /// last value — there is none if the stream was disposed before it ever published — and
    /// then graceful completion, instead of a silent no-op.</returns>
    public virtual IDisposable Subscribe(IObserver<ChangeItem<TStream>> observer)
    {
        // Fallback creation-context capture: a stream constructed off the subscriber's
        // thread (e.g. a reduced stream built on a non-user scheduler) may not have seen
        // a real user at construction. The FIRST real-user subscriber's context is an
        // acceptable fallback — it is only ever USED by Update when the live context is
        // null, and it is strictly better than the null-post storm. Still real-user only.
        if (_creationContext is null && Hub is not null)
            _creationContext = CaptureRealUserContext(Hub);
        try
        {
            var subscription = Store.Synchronize().Subscribe(observer);
            logger.LogDebug("[SYNC_STREAM] Subscribe for {StreamId}, subscription created", StreamId);
            return subscription;
        }
        catch (ObjectDisposedException e)
        {
            // Not the store (completed on disposal, never disposed): this is the OBSERVER's
            // own teardown throwing out of the synchronous replay/completion delivery inside
            // Subscribe — e.g. a disposed Blazor circuit. Benign during teardown.
            logger.LogDebug("[SYNC_STREAM] Subscribe failed for {StreamId} - observer disposed during replay: {Exception}", StreamId, e.Message);
            return new AnonymousDisposable(() => { });
        }
    }

    private bool isDisposed;
    private readonly object disposeLock = new();

    /// <summary>
    /// Everything <see cref="RegisterForDisposal(IDisposable)"/> coupled to THIS STREAM's lifetime,
    /// disposed SYNCHRONOUSLY inside <see cref="Dispose"/>.
    ///
    /// <para>🚨 It used to be the hub's composite instead, and that is a leak, not a detail (#1613).
    /// <c>MessageHub.Dispose()</c> disposes nothing synchronously: it closes hosted-hub creation,
    /// cancels in-flight handlers, posts <c>ShutdownRequest(Quiescing)</c> and RETURNS. The hub's
    /// <c>disposables</c> composite is walked in the ShutDown phase — several posted messages and
    /// action-block turns later, bounded by nothing, on a host that is itself tearing down. So a
    /// registrant whose whole job is to release something the moment the stream dies (the
    /// <c>hub.Observe</c> subscription for the initial <c>SubscribeRequest</c>, whose disposal is
    /// what removes the pending callback from <c>responseSubjects</c>) was released minutes of
    /// scheduling later, or never.</para>
    ///
    /// <para>Locally that never showed, because the callback was not closed by disposal at all — the
    /// owner's reply closed it. It only surfaces when the owner is still <c>Starting</c> and the
    /// delivery sits <c>DEFERRED gates=[DataContextInit,Initialize]</c>: no reply, and the only
    /// remaining closer too slow. That is why it read as a flaky test while being present in every
    /// run.</para>
    ///
    /// <para>The hub registration is KEPT as a backstop — this composite is hooked onto the hub once,
    /// so a hub that tears down WITHOUT the stream being disposed still releases everything.
    /// <see cref="CompositeDisposable"/> is idempotent, so the two paths cannot double-dispose.</para>
    /// </summary>
    private readonly CompositeDisposable streamDisposables = new();

    /// <summary>0 until the composite above has been hooked onto the hub's own disposal (once).</summary>
    private int hubDisposalHooked;
    private readonly ILogger<SynchronizationStream<TStream>> logger;

    /// <summary>
    /// The stream this one was reduced FROM — set by <c>WorkspaceStreams.CreateReducedStream</c>,
    /// null for a stream that is not a reduce of another. INTERNAL on purpose: it exists so
    /// <see cref="StreamLiveness.IsUsable"/> can walk the reduce chain in one place, and is exposed
    /// through <see cref="IStreamLivenessSource"/> rather than the public stream contract.
    /// </summary>
    internal ISynchronizationStream? Source { get; init; }

    /// <inheritdoc />
    ISynchronizationStream? IStreamLivenessSource.Source => Source;

    /// <inheritdoc />
    bool IStreamLivenessSource.IsDisposed => isDisposed;

    /// <inheritdoc />
    bool IStreamLivenessSource.IsFaulted => faulted;

    /// <summary>
    /// Set the instant <see cref="Store"/> takes a terminal error, and never cleared.
    ///
    /// <para>🚨 A FAULT IS FOREVER on this type. <see cref="Store"/> is a
    /// <see cref="ReplaySubject{T}"/>: once it holds an <c>OnError</c>, the Rx grammar says it can
    /// never notify anything else again, and every LATER subscriber replays that same error
    /// immediately. Nothing in the stream re-opens it — <c>Resubscribe</c> re-posts a
    /// <c>SubscribeRequest</c>, but the answer lands in a store that is already terminal.</para>
    ///
    /// <para>That makes a faulted stream exactly as dead as a disposed one, which is why
    /// <see cref="StreamLiveness.IsUsable"/> reads this flag: without it, a cache that keyed a
    /// mirror by (owner, reference, identity) kept serving the corpse for the whole process
    /// lifetime, so ONE unanswered SubscribeRequest turned into a permanent failure of that path
    /// (Systemorph/MeshWeaver#2387).</para>
    /// </summary>
    private volatile bool faulted;

    /// <summary>
    /// The ONE way this stream's store takes a terminal error — it records
    /// <see cref="faulted"/> and then errors the store. Every <c>Store.OnError</c> in this type
    /// goes through here so the flag can never drift from the store's actual state.
    /// </summary>
    /// <param name="error">The terminal error to publish to subscribers.</param>
    private void FaultStore(Exception error)
    {
        faulted = true;
        Store.OnError(error);
    }

    // Mirror of MeshWeaver.Mesh.Security.WellKnownUsers.System — Data sits below
    // Mesh.Contract in the project graph and cannot reference it. Same literal
    // recognised by AccessService.ImpersonateAsSystem.
    private const string SystemUserId = "system-security";

    /// <summary>
    /// The well-known System identity stamped on the FINAL-FALLBACK write of an
    /// <see cref="StreamConfiguration{TStream}.RunsAsInfrastructure">infrastructure</see>
    /// mirror stream (a data-source <see cref="EntityStore"/> store: <c>ds/Activity</c>,
    /// <c>ds/&lt;partition&gt;</c>, …) when no live / creation / owner identity can be
    /// resolved. Equivalent to <see cref="AccessService.ImpersonateAsSystem"/>'s context,
    /// constructed inline because Data sits below Mesh.Contract in the project graph.
    /// </summary>
    private static readonly AccessContext InfrastructureContext =
        new() { ObjectId = SystemUserId, Name = SystemUserId };

    /// <summary>
    /// The real-user <see cref="AccessContext"/> captured ONCE on the thread that
    /// CONSTRUCTS (or first subscribes to) this stream — the circuit / SubscribeRequest
    /// handler thread, where <see cref="AccessService.Context"/> still identifies the
    /// subscribing user. <see cref="Update(System.Func{TStream,MeshWeaver.Data.ChangeItem{TStream}},System.Action{System.Exception})"/> RESTORES it when the LIVE AsyncLocal context
    /// has gone null — a deferred / continuation write (a layout-area render emission, a
    /// watcher tick, an agent streaming hop). Without it those writes posted a NULL
    /// AccessContext, which the never-null PostPipeline guard fails closed: the systemic
    /// "hub=sync/… message=UpdateStreamRequest … no AccessContext" DeliveryFailure storm.
    /// <para>NEVER a hub/system principal — see <see cref="CaptureRealUserContext"/>: an
    /// infrastructure-created stream (no real user) captures null and the existing
    /// PostPipeline fallback still applies, so a hub address can never leak into
    /// <c>CreatedBy</c>.</para>
    /// </summary>
    private AccessContext? _creationContext;

    /// <summary>
    /// The most recently applied change item (the current state snapshot), or <c>null</c> before any value
    /// has been set.
    /// </summary>
    public ChangeItem<TStream>? Current
    {
        get => current;
    }


    /// <summary>
    /// The actual synchronization hub.
    ///
    /// <para>🚨 <b>Bound BEFORE the hub can process its own initialization, not after
    /// <c>GetHostedHub</c> returns</b> (#2625). <c>MessageHubConfiguration.Build</c> ends with
    /// <c>StartMessageProcessing()</c>, which POSTS <c>InitializeHubRequest</c> — so the sub-hub's
    /// BuildupActions run on its turn scheduler while this constructor is still inside
    /// <c>GetHostedHub</c>. A data source whose initial load faults synchronously
    /// (<c>Observable.Throw</c>) reaches <see cref="OnError"/> in that window, and with the
    /// assignment done only on the constructor's return path that call found <c>Hub</c> null and
    /// SILENTLY skipped <c>FailStartup</c> + <c>OpenGate(SynchronizationGate)</c>: the sub-hub
    /// stayed <c>Starting</c> forever, its <c>Started</c> task never settled, so
    /// <c>IDataSource.Initialized</c> hung and the owning <c>DataContext</c>'s gate was never
    /// given its answer — every request to that hub deferred until an unrelated deadline
    /// expired.</para>
    ///
    /// <para>So the binding is a SYNCHRONOUS buildup action (<see cref="BindHub"/>), which
    /// <c>Build</c> runs BEFORE <c>StartMessageProcessing</c>. There is then no window at all:
    /// every message handler and every BuildupAction on the sub-hub sees a bound
    /// <c>Hub</c>, on the same thread that is still running this constructor. The constructor's
    /// own assignment below remains for the path where <c>GetHostedHub</c> hands back a
    /// PRE-EXISTING sub-hub, in which case the configuration lambda — and therefore
    /// <see cref="BindHub"/> — never runs (and no BuildupAction runs either, so nothing can
    /// observe the gap).</para>
    /// </summary>
    public IMessageHub Hub { get; private set; } = null!;

    /// <summary>
    /// Binds <see cref="Hub"/> from the sub-hub's synchronous buildup action — see the remarks
    /// on <see cref="Hub"/> for why this cannot wait for the constructor's return path.
    /// </summary>
    private void BindHub(IMessageHub syncHub) => Hub = syncHub;

    /// <summary>
    /// The host of the synchronization stream.
    /// </summary>
    public IMessageHub Host { get; }



    /// <summary>
    /// The reduce manager used to derive reduced/projected streams and to apply patches for this stream.
    /// </summary>
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

        var valuesEqual = current is not null && ValuesEqual(current.Value, value.Value);

        // 🚨 VALUE-dedup PATCHES ONLY. A FULL that reaches here always applies — it is the owner
        // re-asserting its complete authoritative state (initial snapshot, SetFull overwrite,
        // rollback / resync), so it must land even when value-equal to what THIS stream currently
        // holds: a downstream mirror that optimistically diverged re-converges only if the Full is
        // applied + re-emitted here. Suppressing a value-equal Full is what swallowed the rollback.
        // NOTE: this is VALUE dedup, distinct from the VERSION monotonicity guard in UpdateStream —
        // that guard now drops STALE Fulls (version < Current) BEFORE they reach here, so any Full
        // arriving at this point is already version-current/ahead and must be applied.
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

    /// <summary>
    /// Value-EQUIVALENCE comparison for the patch-dedup gate — NOT reference/struct identity.
    /// <para>🚨 <see cref="System.Text.Json.JsonElement"/> (the payload of EVERY layout-area
    /// <c>SynchronizationStream&lt;JsonElement&gt;</c>) and <see cref="System.Text.Json.Nodes.JsonNode"/>
    /// have NO value <c>Equals</c>: the struct/reference default is never equal for two
    /// equal-CONTENT instances. Each reduce/render allocates a fresh instance, so
    /// <c>object.Equals</c> returned false on every hop → the dedup at <see cref="SetCurrent"/>
    /// never fired → an identical re-render re-posted <c>SetCurrentRequest</c> and fanned out
    /// across every mirror hub (a ~4.5k-message/3s storm that saturated the single-threaded
    /// action blocks and starved the real terminal transition past the consumer's timeout — the
    /// "streaming cell never clears" wedge). Compare by content via <c>DeepEquals</c> instead.</para>
    /// <para>Falls back to <c>object.Equals</c> for any other <c>TStream</c> so behaviour is
    /// unchanged for non-JSON payloads — this can only ADD correct dedup, never suppress a genuine
    /// change (DeepEquals is true only when the content is byte-for-byte equivalent).</para>
    /// </summary>
    private static bool ValuesEqual(TStream? a, TStream? b)
    {
        if (a is null || b is null)
            return ReferenceEquals(a, b);
        try
        {
            // JsonElement is how MeshNode.Content AND every layout-area EntityStore ride — compare
            // by structure, hand-rolled (NOT the .NET 9 JsonElement.DeepEquals intrinsic, absent on an
            // older runtime → MissingMethodException). NEVER throw: a fault on this stream path failed
            // grain activation in the distributed runtime, so any comparison error degrades to
            // "changed" (emit), never an exception.
            if (a is System.Text.Json.JsonElement ae && b is System.Text.Json.JsonElement be)
                return JsonDeepEquals(ae, be);
            if (a is System.Text.Json.Nodes.JsonNode an && b is System.Text.Json.Nodes.JsonNode bn)
                return an.ToJsonString() == bn.ToJsonString();
            return Equals(a, b);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Recursive structural equality over <see cref="System.Text.Json.JsonElement"/>:
    /// objects (same keys, equal values), arrays (same length, element-wise, order-sensitive),
    /// primitives by value. Independent of the .NET 9 <c>DeepEquals</c> intrinsic.</summary>
    private static bool JsonDeepEquals(System.Text.Json.JsonElement a, System.Text.Json.JsonElement b)
    {
        if (a.ValueKind != b.ValueKind)
            return false;
        switch (a.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
            {
                var countA = 0;
                foreach (var pa in a.EnumerateObject())
                {
                    countA++;
                    if (!b.TryGetProperty(pa.Name, out var bv) || !JsonDeepEquals(pa.Value, bv))
                        return false;
                }
                var countB = 0;
                foreach (var _ in b.EnumerateObject())
                    countB++;
                return countA == countB;
            }
            case System.Text.Json.JsonValueKind.Array:
            {
                var ea = a.EnumerateArray();
                var eb = b.EnumerateArray();
                while (true)
                {
                    var na = ea.MoveNext();
                    var nb = eb.MoveNext();
                    if (na != nb)
                        return false;
                    if (!na)
                        return true;
                    if (!JsonDeepEquals(ea.Current, eb.Current))
                        return false;
                }
            }
            case System.Text.Json.JsonValueKind.String:
                return a.GetString() == b.GetString();
            case System.Text.Json.JsonValueKind.Number:
                return a.GetRawText() == b.GetRawText();
            default:
                // True / False / Null / Undefined — the matched ValueKind alone determines equality.
                return true;
        }
    }

    private const string SynchronizationGate = nameof(SynchronizationGate);
    /// <summary>
    /// Low-level write: posts a transform that maps the current state to the next change item to the
    /// stream's hub, where it is applied serially. Captures the caller's access context (falling back to
    /// the creation/owner/infrastructure identity) so the write runs under the correct identity even on a
    /// deferred continuation. A dead/disposed stream signals the producer via <paramref name="exceptionCallback"/>.
    /// </summary>
    /// <param name="update">Maps the current state to the change to apply, or <c>null</c> for a no-op.</param>
    /// <param name="exceptionCallback">Invoked synchronously if the write fails.</param>
    public void Update(Func<TStream?, ChangeItem<TStream>?> update, Action<Exception> exceptionCallback)
        => Update(update, exceptionCallback, null);

    /// <inheritdoc cref="ISynchronizationStream{TStream}.Update(System.Func{TStream,ChangeItem{TStream}},System.Action{System.Exception},System.Action)"/>
    public void Update(Func<TStream?, ChangeItem<TStream>?> update, Action<Exception> exceptionCallback, Action? applied)
    {
        if (!TryGetActiveHub(out var hub))
        {
            SignalDisposedToProducer(exceptionCallback);
            return;
        }
        // A present LIVE context always wins; only fall back to the captured creation
        // context when the live AsyncLocal is null (the deferred/continuation case — a
        // layout-area render emission, a watcher tick, an agent streaming hop). This
        // restores the subscribing user's identity instead of posting a null AccessContext
        // that the never-null PostPipeline guard would fail closed (the storm).
        var capturedContext = CaptureCallerAccessContext(hub) ?? _creationContext;
        if (capturedContext is null)
        {
            // Owner-injection fallback at the chokepoint. The live AsyncLocal is gone (a deferred /
            // cross-hub continuation) AND the construction-time capture (_creationContext) was null —
            // the stream was built before its OWNING hub established a standing identity (the
            // cold-activation race: SetThreadHubIdentity / the per-node owner identity resolves the
            // owner ASYNC, after the stream ctor ran, so the ctor's CaptureRealUserContext(Host) saw
            // nothing). Re-capture from Host at WRITE time: by the time a real write happens the owning
            // per-node hub carries its OWNER as its standing CircuitContext, so Host now yields it.
            // Cache it back so later writes skip the lookup. Real-user ONLY (CaptureRealUserContext
            // refuses null / IsHub / hub-shaped / system), so a hub/system Host still yields null and
            // the never-null PostPipeline guard fails the write closed — the
            // StreamUpdate_WithoutAsyncLocalIdentity_FailsClosed invariant holds. This carries the owner
            // across the async boundary that previously dropped it (the CI-only deferred owner-side sync
            // write — InboxToolIntegrationTest.Cancel and similar).
            capturedContext = _creationContext = CaptureRealUserContext(Host);
            if (capturedContext is null)
            {
                // Still nothing — the cold-start FIRST write, where Host has not yet established its
                // standing identity (SetThreadHubIdentity is async and lost the race). The node itself is
                // ALREADY in Current at write time, so resolve the OWNER from it directly (CreatedBy) via
                // the MeshNode-aware IStreamOwnerResolver registered on the owning hub. Race-free: no async
                // round-trip, the node is in hand. Filtered through IsRealUser so a hub/system CreatedBy
                // can never leak in and the fail-closed invariant holds when no real owner exists.
                var resolved = Host?.ServiceProvider?.GetService<IStreamOwnerResolver>()
                    ?.ResolveOwner(Current is null ? default : Current.Value, Host.Address);
                if (IsRealUser(resolved))
                    capturedContext = _creationContext = resolved;
            }
        }
        // FINAL fallback for a genuine INFRASTRUCTURE mirror stream (a data-source EntityStore
        // store: ds/Activity, ds/<partition>, …). By the time a change reaches the data-source
        // mirror it is ALREADY AUTHORIZED — RLS was enforced at the user-facing write — and the
        // store carries MANY nodes (different owners), so the owner-resolver above legitimately
        // returns null. Such a deferred / cross-hub propagation whose live AsyncLocal context is
        // gone must NOT post a context-less UpdateStreamRequest: the never-null PostPipeline guard
        // fails it closed → Store.OnError → the stream's ReplaySubject is terminally faulted and
        // every FUTURE subscriber replays only the error, never a Full (the blank-side-panel-until-
        // reload bug). Stamp System — the SAME rule and fix as DataSourceWithStorage's persistence
        // hub and VirtualDataSource's mirror writes. NOT real-user writes: a live / creation / owner
        // context above always wins; this only fills the genuine no-user gap. Deliberately NOT cached
        // into _creationContext (which stays real-user only, never System).
        if (capturedContext is null && Configuration.RunsAsInfrastructure)
            capturedContext = InfrastructureContext;
        hub.Post(
            new UpdateStreamRequest(update, exceptionCallback, applied),
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
    /// <summary>
    /// The version this stream stamps on a frame it ORIGINATES. 🚨 ONE clock per stream:
    /// the OWNING hub's monotonic <c>Hub.Version</c> (this stream's own sync hub), never the
    /// parent <c>Host.Version</c>. A subscriber (non-owner) only carries the base it last read.
    /// <para>
    /// Every frame an owned stream emits — the init/base frame (<see cref="Initialize"/>), a
    /// value <see cref="Update(System.Func{TStream,TStream},System.Action{System.Exception})"/>,
    /// and a layout-area render push (<c>LayoutAreaHost.PushRenderResult</c>) — MUST come off
    /// this same clock. Mixing <c>Host.Version</c> (the parent host hub, which runs far ahead)
    /// into the init frame while content frames use <c>Hub.Version</c> stamped the base frame
    /// with a HIGHER version than the render that follows; the receive-side monotonicity guard
    /// (drops a Full whose version &lt; Current) then discarded every render Full as "stale",
    /// so a freshly-subscribed layout area stayed stuck on the "Building layout…" base frame and
    /// never emitted its content (the DataChangeStreamUpdateTest count-view non-emission).
    /// </para>
    /// </summary>
    private long OwnerVersion()
        => Owner.Equals(Host.Address) ? Hub.Version : (Current?.Version ?? 0L);

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
        var version = OwnerVersion();

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
    /// <see cref="ChangeType.Full"/> (a complete-state overwrite rather than a per-entity delta).
    /// Like any change it carries the owner's monotonic <c>Hub.Version</c> and is subject to the
    /// receive-side monotonicity guard, so a mirror already AHEAD (a newer applied version) is not
    /// clobbered by an older Full. See
    /// <see cref="SetFull(System.Func{TStream,TStream},System.Action{System.Exception})"/>.
    /// </summary>
    private ChangeItem<TStream> BuildFullChangeItem(TStream? current, TStream updated)
    {
        // ChangedBy = ClientId always (never empty; matches the echo-suppression filters,
        // never the per-instance StreamId). AccessContext is orthogonal. See BuildChangeItem.
        var changedBy = ClientId;
        // 🚨 ONLY the owning hub sets Version. Subscriber carries the base it read.
        var version = OwnerVersion();

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
    private AccessContext? CaptureCallerAccessContext(IMessageHub hub)
        => CaptureCallerAccessContext(hub, Owner);

    /// <summary>
    /// Context capture: the live delivery context, then this flow's circuit / single-identity
    /// host, then OWNER INJECTION for the node this stream belongs to, then for the running hub.
    ///
    /// <para>🚨 <paramref name="owner"/> is looked up BEFORE <paramref name="hub"/> and is the arm
    /// that actually fires: a deferred sync write runs on the SYNC hub, whose address is not the
    /// node's, so a hub-keyed lookup misses and the write posts a null AccessContext (failed closed
    /// by the never-null guard — the cold-start submit deadlock). The stream's Owner IS the node
    /// path that <c>SetStandingIdentity</c> registered under.</para>
    /// </summary>
    private static AccessContext? CaptureCallerAccessContext(IMessageHub hub, Address? owner)
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
            return accessService?.Context
                   ?? accessService?.CircuitContext
                   ?? accessService?.GetStandingIdentity(owner?.ToFullString())
                   ?? accessService?.GetStandingIdentity(hub);
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Captures the caller's <see cref="AccessContext"/> but ONLY when it identifies a
    /// REAL USER — not null/empty, not a hub-shaped principal (sync/…, mesh/…, node/…,
    /// activity/…, portal/…), not a hub credential (<see cref="AccessContext.IsHub"/>),
    /// and not the well-known System identity. Seeds <see cref="_creationContext"/>: a
    /// creation context restored on a continuation write MUST be a real user — capturing a
    /// hub address would re-introduce the "CreatedBy=sync/xxx" leak, and capturing System
    /// would silently run every continuation with Permission.All. An infrastructure-created
    /// stream (no real user) therefore captures null and the existing PostPipeline fallback
    /// still applies.
    /// </summary>
    private AccessContext? CaptureRealUserContext(IMessageHub hub)
    {
        var ctx = CaptureCallerAccessContext(hub);
        return IsRealUser(ctx) ? ctx : null;
    }

    private static bool IsRealUser(AccessContext? ctx)
        => ctx is not null
           && !ctx.IsHub
           && !string.IsNullOrEmpty(ctx.ObjectId)
           && !AccessService.LooksLikeHubPrincipal(ctx.ObjectId)
           && !string.Equals(ctx.ObjectId, SystemUserId, StringComparison.Ordinal);

    /// <summary>
    /// Resolves the synchronization hub if the stream is still alive. A stream that has been
    /// DISPOSED must not post — its hub is gone — so callers no-op gracefully instead.
    /// <para>The <c>Hub is null</c> arm is belt-and-braces only: since the constructor REFUSES
    /// (<see cref="HubDisposingException"/>) rather than fabricating a hub-less "dead stream",
    /// a constructed instance always has a hub. Cheap to keep, and it documents that nothing
    /// downstream may assume otherwise.</para>
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

    /// <summary>
    /// Completes the stream, signalling no further change items to subscribers. Safe at any
    /// point of teardown: the store is COMPLETED on disposal, never disposed (#1170/#1171),
    /// and a completed subject ignores further terminal notifications per the Rx grammar — so
    /// a completion draining out of a concurrently-disposing upstream chain lands as a no-op.
    /// (The previous <c>!Store.IsDisposed</c> pre-check was check-then-act across threads and
    /// could not close that window; with the store never disposed, no window exists.)
    /// </summary>
    public void OnCompleted()
    {
        Store.OnCompleted();
    }

    /// <summary>
    /// Faults the stream with an error, propagating it to subscribers and failing hub startup. Benign
    /// teardown (<see cref="ObjectDisposedException"/>) and transient hub timeouts are logged quietly.
    /// </summary>
    /// <param name="error">The error to propagate.</param>
    public void OnError(Exception error)
    {
        // Gate on the stream's own disposal flag (set-once under disposeLock), NOT the
        // subject's state: a disposed stream must not fault its (already-disposing) hub's
        // startup or reopen its gate. The store itself is completion-terminated on disposal
        // and ignores a racing OnError per the Rx grammar, so the branch below is safe even
        // when disposal lands concurrently after this check.
        if (!isDisposed)
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
                FaultStore(error);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "[SYNC_STREAM] Exception from Store.OnError propagation for {StreamId}", StreamId);
            }
            // Always fault startup and open gate, even if Store.OnError throws. These two calls
            // are the ONLY thing that lets a faulted initial load settle
            // IDataSource.Initialized — FailStartup faults the sub-hub's Started task, which
            // DataContext's gate is a WhenAll over.
            //
            // 🚨 SKIPPING THEM IS A WEDGE, NEVER A NO-OP (#2625). Hub is bound by a SYNCHRONOUS
            // buildup action now (see the remarks on Hub), so it is non-null for every
            // BuildupAction and every handler and this branch cannot be taken from the init
            // path. It stays as a guard because the constructor's refusal path leaves a
            // stream whose Hub was never bound — but it must be LOUD: the silent version cost
            // #2625 two months of an unreproducible CI flake, because everything downstream
            // simply waited forever with nothing logged.
            if (Hub is not null)
            {
                Hub.FailStartup(error);
                Hub.OpenGate(SynchronizationGate);
            }
            else
            {
                logger.LogError(error,
                    "[SYNC_STREAM] OnError for {StreamId} (Reference={Reference}, Owner={Owner}) "
                    + "could not fault its sub-hub's startup: the stream has no Hub bound. Anything "
                    + "waiting on that hub's Started task — IDataSource.Initialized, and therefore "
                    + "the owning DataContext's initialization gate — will never be settled by this "
                    + "fault.", StreamId, Reference, Owner);
            }
        }
        else
        {
            // Stream already disposed — benign during teardown, never log Warning.
            logger.LogDebug("[SYNC_STREAM] OnError skipped for {StreamId} - stream is disposed", StreamId);
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

    /// <summary>
    /// Couples the lifetime of <paramref name="disposable"/> to this stream: it is disposed when the stream
    /// is disposed, or immediately if the stream is already dead.
    /// </summary>
    /// <param name="disposable">The disposable to dispose with the stream.</param>
    public void RegisterForDisposal(IDisposable disposable)
    {
        if (isDisposed || Hub is null)
        {
            // Disposed stream — no hub to register on. Dispose the registrant
            // immediately so the caller doesn't leak it. The caller's intent
            // (couple this disposable to the stream's lifetime) is satisfied
            // because the stream is already terminal.
            try { disposable.Dispose(); } catch { /* best-effort */ }
            return;
        }

        // Hook the stream's composite onto the hub ONCE — the backstop for a hub that tears down
        // without Dispose() ever being called on the stream. Everything else rides the composite,
        // which Dispose() walks SYNCHRONOUSLY (see the field note).
        if (Interlocked.Exchange(ref hubDisposalHooked, 1) == 0)
            Hub.RegisterForDisposal(streamDisposables);

        // CompositeDisposable.Add disposes the registrant immediately if the composite is already
        // disposed, so a registration racing Dispose() cannot leak.
        streamDisposables.Add(disposable);
    }

    /// <summary>
    /// Forwards a message delivery to the stream's hub for processing.
    /// </summary>
    /// <param name="delivery">The delivery to forward.</param>
    /// <returns>The processed delivery, or a failed delivery if the stream is dead/disposed.</returns>
    public IMessageDelivery DeliverMessage(IMessageDelivery delivery)
    {
        if (isDisposed || Hub is null)
        {
            logger.LogDebug("[SYNC_STREAM] DeliverMessage skipped for {StreamId} — stream is dead/disposed", StreamId);
            return delivery.Failed("Stream is disposed");
        }
        return Hub.DeliverMessage(delivery.ForwardTo(Hub.Address));
    }


    /// <summary>
    /// Pushes a change item into the stream by posting it to the stream's hub (as a SetCurrentRequest).
    /// A dead/disposed stream drops the value; a post failure is forwarded to subscribers via the store's error channel.
    /// </summary>
    /// <param name="value">The change item to publish.</param>
    public void OnNext(ChangeItem<TStream> value)
    {
        // A DISPOSED stream has no hub to post to; drop the value rather than
        // NRE'ing on Hub.Post. (Subscribers already saw Store.OnCompleted.)
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
                // Safe on a disposed stream: the store is completion-terminated on disposal,
                // never disposed, and a terminated subject ignores OnError (Rx grammar).
                FaultStore(ex);
            }
            catch
            {
                // A subscriber's OnError handler may throw — best effort.
            }
        }
    }

    /// <summary>
    /// Requests a change to the stream (the validation-aware entry point), currently delegating to
    /// <see cref="Update(System.Func{TStream,ChangeItem{TStream}},System.Action{System.Exception})"/>.
    /// </summary>
    /// <param name="update">Maps the current state to the change to apply, or <c>null</c> for a no-op.</param>
    /// <param name="exceptionCallback">Invoked synchronously if the change fails.</param>
    public virtual void RequestChange(Func<TStream?, ChangeItem<TStream>?> update, Action<Exception> exceptionCallback)
    {
        // TODO V10: Here we need to inject validations (29.07.2024, Roland Bürgi)
        Update(update, exceptionCallback);
    }

    /// <summary>
    /// Creates a synchronization stream hosted under <paramref name="Host"/>. Spins up a dedicated
    /// hosted hub for the stream and captures the creating user's identity.
    /// </summary>
    /// <param name="StreamIdentity">Identity (owner + partition) of the stream.</param>
    /// <param name="Host">The hub hosting this stream.</param>
    /// <param name="Reference">The projected reference selecting what this stream represents.</param>
    /// <param name="ReduceManager">The reduce manager for deriving reduced streams and applying patches.</param>
    /// <param name="configuration">Optional configuration of the stream (client id, subscriber, initialization, …).</param>
    /// <exception cref="HubDisposingException">
    /// <paramref name="Host"/> (or an ancestor of it) has begun disposing, so the stream's own
    /// sub-hub can no longer be created. TRANSIENT — see the constructor body.
    /// </exception>
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

        // 🚨 Resolve the logger DEFENSIVELY, and keep it FIRST. Both halves matter.
        //
        // Defensively, because a hub that has finished disposing no longer HAS a container — its
        // lifetime scope is closed as the last act of its teardown
        // (HostedHubsCollection.CloseScopeWhenDisposed). A hard `GetRequiredService` there throws
        // Autofac's raw `ObjectDisposedException: … LifetimeScope … already disposed`, which is the
        // same family as the refusal below but names the CONTAINER instead of the hub, carries no
        // HubAddress for the caller to retry, and never reaches the ErrorType.ShuttingDown
        // classification. This is not re-wrapping that failure: the refusal still comes from the
        // gate, as a HubDisposingException. Only the LOGGER degrades, to Null.
        //
        // First, because moving it below the gate is a regression I actually shipped into a branch:
        // the gate calls `GetHostedHub(…, ConfigureSynchronizationHub, …)`, which CONSTRUCTS the
        // sync hub and runs its BuildupActions — and those reach back into stream code that logs.
        // With the assignment moved down, that ran against a null field and every buildup faulted
        // with `ArgumentNullException … (Parameter 'logger')`, leaving each hub FAILED and every
        // observable emitting nothing. 34 of 34 DataTest cases went from green to timing out.
        logger = ResolveLogger(Host);

        logger.LogDebug("Creating Synchronization Stream {StreamId} for Host {Host} and {StreamIdentity} and {Reference}", StreamId, Host.Address, StreamIdentity, Reference);

        // A stream that cannot own its sub-hub is NOT a stream — REFUSE, never fabricate.
        //
        // The condition below is one fact: hosted-hub creation is frozen, so
        // GetHostedHub cannot give us the Hub this type declares as non-null. The freeze is
        // flipped by HostedHubsCollection.CloseCreation at the FIRST instant of
        // MessageHub.Dispose — strictly BEFORE RunLevel leaves Started — and it CASCADES
        // through the entire hosted-hub subtree, so an ancestor's disposal freezes us while
        // our own RunLevel still reads Started. That is why the RunLevel probe alone is not
        // sufficient and why GetHostedHub returning null is the authoritative signal.
        //
        // The predecessor of this code answered that by building a DEAD stream — isDisposed,
        // Store completed, `Hub = null!` — and documenting that "every code path that touches
        // Hub goes through TryGetActiveHub". NO CONSUMER HONOURED THAT: `grep -rn
        // TryGetActiveHub src` found only this file, while ~96 sites dereference
        // ISynchronizationStream.Hub, which the interface declares NON-NULLABLE. And a
        // consumer cannot even detect the corpse: ISynchronizationStream exposes no
        // liveness/disposal member. Handing one out is a contract violation by construction —
        // it cost a production NRE inside LayoutAreaHost's constructor
        // (`Stream.Hub.ServiceProvider…`) on the overlay self-heal recycle window, which
        // escaped to the subscriber as a TERMINAL DeliveryFailure. A page that subscribed
        // during a recycle got "this failed forever" instead of "ask again".
        //
        // Throwing is also what the sibling creation path already does — CreateExternalClient
        // has always thrown here, and the Blazor circuit already catches ObjectDisposedException
        // around BindStream() for exactly this ("old circuit's hub may already be disposing").
        // HubDisposingException IS an ObjectDisposedException, so that existing handling keeps
        // working; what is new is the CLASSIFICATION: escaping a message handler it becomes an
        // ErrorType.ShuttingDown DeliveryFailure (MessageService), i.e. the transient "the
        // address may reactivate — retry" answer #672 established one layer down, instead of a
        // terminal fault. See Doc/Architecture/HubDisposalModel.
        //
        // The RunLevel probe stays as the cheap FIRST gate — it also covers the (vanishingly
        // rare) case where GetHostedHub would hand back a PRE-EXISTING sub-hub at our address
        // while the Host is already winding down: an existing-hub lookup is a pure read and is
        // deliberately not refused by the freeze.
        var syncHub = Host.RunLevel > MessageHubRunLevel.Started
            ? null
            : Host.GetHostedHub(
                SynchronizationAddress.Create(ClientId), ConfigureSynchronizationHub, HostedHubCreation.Always);
        if (syncHub is null)
        {
            logger.LogDebug(
                "[SYNC_STREAM] Cannot host stream for {Reference} on {Host} (RunLevel={RunLevel}, hosted-hub creation frozen); refusing to create the stream",
                Reference, Host.Address, Host.RunLevel);
            isDisposed = true;
            Store.OnCompleted();
            throw new HubDisposingException(Host.Address, Reference);
        }
        Hub = syncHub;

        // The outstanding fresh-snapshot re-ask dies with the stream — a pending Observe callback
        // that outlives it is exactly the leaked callback the quiescing budget flags.
        RegisterForDisposal(resyncSubscription);

        // 🚨 Capture the creating user's identity ONCE, here on the thread that constructs
        // the stream — in production that is the circuit thread (cache.GetMeshNodeStream)
        // or the owner's SubscribeRequest handler, where AccessService.Context is the real
        // subscribing user. Update() restores it for deferred/continuation writes whose
        // live AsyncLocal context has gone null. Real users only (CaptureRealUserContext):
        // an infrastructure-created stream captures null and falls back to the existing
        // PostPipeline behaviour — a hub/system principal is never captured.
        _creationContext = CaptureRealUserContext(Host);
    }

    /// <summary>
    /// The stream's logger, resolved from <paramref name="host"/> — or
    /// <see cref="NullLogger{T}"/> when the host's lifetime scope has already been closed.
    ///
    /// <para>NEVER returns null, and that is the contract that matters: the field it fills is read
    /// from the constructor onward, including by the sync hub's BuildupActions, which run while the
    /// constructor is still executing. A null there faults the buildup and leaves the hub FAILED —
    /// a whole-suite outage produced by a missing log line.</para>
    /// </summary>
    private static ILogger<SynchronizationStream<TStream>> ResolveLogger(IMessageHub host)
    {
        try
        {
            return host.ServiceProvider.GetService<ILogger<SynchronizationStream<TStream>>>()
                   ?? NullLogger<SynchronizationStream<TStream>>.Instance;
        }
        catch (ObjectDisposedException)
        {
            // The host's container is gone. Losing the debug line is the correct price; the caller
            // is about to be refused with HubDisposingException, which carries the same two facts.
            return NullLogger<SynchronizationStream<TStream>>.Instance;
        }
    }

    private MessageHubConfiguration ConfigureSynchronizationHub(MessageHubConfiguration config)
    {
        config = config
            // 🚨 FIRST, and the SYNCHRONOUS overload on purpose (#2625): SyncBuildupActions run
            // inside Build() BEFORE StartMessageProcessing posts InitializeHubRequest, so this
            // binds the stream's Hub before ANY message — including this hub's own init, whose
            // fault path calls OnError → Hub.FailStartup — can reach code that reads it. See the
            // remarks on Hub.
            .WithInitialization(BindHub)
            // Inherit the owning Host's posting identity (feedback_access_context_always_set). In
            // prod the Host is a User hub (= the default) so this is a no-op; in plumbing tests the
            // Host is a System hub and the sync hub must be System too, else its own
            // UpdateStreamRequest posts carry no AccessContext and the never-null guard fails them
            // closed ("hub=sync/… message=UpdateStreamRequest … no AccessContext").
            .WithPostingIdentity(Host.Configuration.PostingIdentity)
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
                    _ = hub.GetWorkspace().RequestChange(delivery.Message);
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
                    // 🚨 A TRANSIENT shutdown reject is NOT terminal for a sync stream. It
                    // means one delivery (typically our SubscribeRequest) raced the owner
                    // hub's DisposeRequest during a recycle/restart — the address is about
                    // to reactivate. This stream's own recovery machinery (keep-alive +
                    // the change-feed resubscribe latch) re-sends the SubscribeRequest
                    // once the fresh activation announces itself, so the right reaction is
                    // to RIDE IT OUT. Calling OnError here tore that machinery down with
                    // the stream: the latch never fired, nothing rehydrated, and every
                    // reader of a mid-recycle NodeType waited to its timeout
                    // (CI 30003419841, NodeTypeCompileParkTest.RecycleRetry — the exact
                    // regression the ShuttingDown ErrorType exists to prevent). All other
                    // failure kinds (RLS denial, validation, NotFound, …) stay terminal.
                    if (failure.ErrorType == ErrorType.ShuttingDown)
                    {
                        logger.LogDebug(
                            "Stream {StreamId} received transient shutdown reject: {Message} — keeping the stream alive for the resubscribe latch",
                            StreamId, failure.Message);
                        return delivery.Processed();
                    }
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
            }).WithHandler<UpdateStreamRequest>((hub, request) =>
            {
                // Fully synchronous — the update func is a pure in-memory transform
                // (IO-producing callers pool their IO FIRST and pass only the result,
                // see ContentCollection.IngestContentFile). No async handler: an awaited
                // update on the hub action block is the deadlock class
                // AsynchronousCalls.md exists to kill.
                var update = request.Message.Update;
                var exceptionCallback = request.Message.ExceptionCallback;
                var applied = request.Message.Applied;
                try
                {
                    // Read the current state right before invoking the update function
                    // This ensures we have the latest state including any updates that occurred
                    // while previous updates were being processed
                    var currentValue = Current is null ? default : Current.Value;
                    var newChangeItem = update.Invoke(currentValue);

                    // SetCurrent will be called with the computed result
                    // The Message Hub serializes these messages, so only one UpdateStreamRequest
                    // is processed at a time per stream, preventing race conditions
                    SetCurrent(hub, newChangeItem);

                    // Post-apply, SAME turn: the writer's "committed" signal cannot observe the
                    // pre-change state, and costs no extra hub message. Guarded like the exception
                    // callback — a writer that throws from its own signal must not kill the turn.
                    if (applied is not null)
                    {
                        try { applied.Invoke(); }
                        catch (Exception cbEx)
                        {
                            logger.LogError(cbEx,
                                "[SYNC_STREAM] applied callback threw on {StreamId}", StreamId);
                        }
                    }
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
                        // 🚨 OwnerVersion() (this stream's Hub.Version), NOT Host.Version. The
                        // init/base frame and every subsequent render frame MUST ride one clock —
                        // see OwnerVersion. Stamping the base frame with the parent host hub's
                        // (far-higher) version made the monotonicity guard drop the lower-versioned
                        // render Fulls as stale, so a late layout-area subscriber stayed stuck on
                        // "Building layout…" and never emitted its content.
                        SetCurrent(hub, new ChangeItem<TStream>(value, StreamId, OwnerVersion()));
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
                    // Same one-clock invariant as the observable-init path above: OwnerVersion(),
                    // never Host.Version, so the init frame can't outrank later owned writes.
                    SetCurrent(hub, new ChangeItem<TStream>(init, StreamId, OwnerVersion()));
                    return System.Reactive.Unit.Default;
                })
                // 🚨 A faulted initial load must fault the STREAM, not only this hub's buildup.
                // Without this hook the error stopped in HandleInitialize's .Catch: the hub
                // entered its FAILED state, but SynchronizationGate stayed shut, RunLevel never
                // reached Started, and Hub.Started never settled — so IDataSource.Initialized
                // (Task.WhenAll over stream-hub Started tasks) HUNG and the owning DataContext
                // could not tell a faulted init from a hung one until its time-box expired 120s
                // later, reporting a TimeoutException instead of the real error. OnError routes
                // the fault where the stream's other failure paths already go: it classifies
                // (teardown stays quiet), faults the store, calls Hub.FailStartup (Initialized
                // settles NOW, with the actual exception) and opens SynchronizationGate. The
                // error still propagates to the buildup's .Catch, which records the hub-level
                // FAILED state. Pinned by DataContextInitFaultedTest (#2528).
                .Do(_ => { }, OnError);
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

        // 🚨🚨 #325 symptom-2 (multi-replica): CONSUME the resubscribe-accept latch on the FIRST
        // Full after a version-gated resubscribe. A mirror that resubscribes did so because it
        // DETECTED it is behind the owner (the change-feed announced a HIGHER node version than the
        // mirror holds — CreateExternalClient's version-gated Resubscribe). The owner's response is a
        // FRESH authoritative snapshot; but after the owner grain idle-recycled its per-activation
        // Hub.Version RESET to ~0, so that fresh Full carries a FRAME version BELOW the mirror's
        // cached (pre-recycle) one and the monotonicity guard below would DROP it — the orphaned-
        // mirror residual of #325. The latch says "I asked for this fresh Full; accept it and adopt
        // the owner's re-based clock" — the resubscribe-Current-reset the guard was missing. Only a
        // FULL consumes it (a stray reordered patch must still be dropped), and it is set ONLY when
        // the mirror is genuinely behind (receivedVersion < announcedVersion), so it can never
        // clobber a newer optimistic write with a stale snapshot (that case keeps the gate CLOSED).
        //
        // 🚨🚨 …AND THE SAME ACCEPTANCE IS OWED TO A FULL A MIRROR WITH NO SNAPSHOT RECEIVES (#2654).
        // `currentJson is null` says the mirror is holding NOTHING it could apply a patch onto —
        // either it never had a snapshot, or RequestFreshSnapshot DISCARDED it before it
        // re-asked. In that state a rebased Full can clobber nothing, and refusing it leaves the
        // mirror with nothing at all: strictly worse. This is the case the guard used to lose. When
        // the owner has to REBUILD the server-side stream to answer a re-ask (the subscriber was
        // evicted on the router's TargetUnserved verdict, #2620; the owner grain recycled), that
        // fresh stream's frame clock starts from scratch, so the snapshot the mirror ASKED FOR is
        // stamped below what it still holds in Current — and the guard threw it away. The gate
        // stayed shut, every later Patch was swallowed at Debug on the "Patch before base Full"
        // branch, and the area sat on its placeholder forever with nothing logged above Debug.
        // Same reasoning as ExpectResubscribeFull, taken off the STATE that justifies it rather
        // than off a separately-armed latch — and off the CACHE rather than the in-flight gate, so
        // it holds however the answer and the gate's release interleave.
        var currentJson = Get<JsonElement?>();
        var acceptRebasedFull = delivery.Message.ChangeType == ChangeType.Full
            && (Interlocked.Exchange(ref _acceptResubscribeFull, 0) == 1 || currentJson is null);

        // 🚨🚨 DIRECTION, not just shape — Systemorph/MeshWeaver#2701. `Version` means TWO DIFFERENT
        // THINGS depending on which way the message travels, and everything below turns on the
        // difference:
        //   • DataChangedEvent (OWNER → mirror) carries the OWNER's clock. It is comparable to
        //     Current.Version, and it is the version this stream must adopt.
        //   • PatchDataChangeRequest (SUBSCRIBER → owner) carries the BASE the subscriber last
        //     APPLIED (StandardReducers.PatchJsonElement stamps `stream.Current?.Version`) — a
        //     value that is BELOW the owner's clock by construction whenever an owner frame is in
        //     flight. It is NOT comparable, and it must never become the owner's clock.
        // This is the same asymmetry the frame-loss check further down already draws in so many
        // words ("Applies to DataChangedEvent (owner→mirror) only: a PatchDataChangeRequest's chain
        // is stamped by the SENDING mirror and is not comparable to this stream's applied
        // version") — the version handling simply never got it.
        var isOwnerFrame = delivery.Message is not PatchDataChangeRequest;

        // 🚨 Monotonicity guard — applies to PATCHES *and* FULLS. Version is ALWAYS the OWNER
        // hub's Version (BuildChangeItem/BuildFullChangeItem: the owner stamps its monotonic
        // Hub.Version — ++ per message, MessageHub.HandleMessageAsync — while a subscriber only
        // ever CARRIES the base version it read; it NEVER bumps it). So Current.Version is the
        // owner's clock and a change with version < it is STALE:
        //   • a reordered OLDER patch (would corrupt the mirror), OR
        //   • a resubscribe's point-in-time Full snapshotted BEFORE a write we have since applied
        //     — without this guard that stale Full lands and overwrites the newer state, the
        //     lost-message data-loss race.
        // A reject→ROLLBACK Full is NOT stale: the owner re-asserts its CURRENT state, stamped with
        // its current (higher-or-equal) Version, so it passes the guard and still lands. Because the
        // subscriber never bumps ahead of the owner, a legitimate Full can never carry a version
        // BELOW Current — only a genuinely older snapshot can, and that is exactly what we drop —
        // EXCEPT the rebased-resubscribe Full latched above (a recycled owner's fresh snapshot).
        //
        // 🚨 …AND EXCEPT A SUBSCRIBER'S OWN WRITE (#2701). Every clause above reasons about a frame
        // the OWNER produced. A PatchDataChangeRequest is the opposite direction: the base version
        // it carries is older than the owner's clock precisely BECAUSE the write is optimistic —
        // the contract this stream documents on Update() is "a subscriber carries the BASE version
        // it last observed so the owner can fast-forward (base == current) or MERGE (base <
        // current)". The guard was swallowing the merge case before the merge code could see it, at
        // Debug, with no rollback Full and no failure to the writer: the user's edit was gone and
        // the re-render it would have produced never happened, so the view sat silent forever. The
        // measured shape is EditorTest.TestEditorWithDelayed — five UpdatePointer writes landing
        // while the 100 ms delayed render's frame is in flight, and a control stream that then
        // emits nothing at all. A subscriber patch that cannot be applied is not silently dropped
        // here: the applier below throws and SyncFailed answers the writer.
        if (isOwnerFrame && Current is not null && delivery.Message.Version < Current.Version && !acceptRebasedFull)
        {
            logger.LogDebug(
                "[SYNC_STREAM] Dropping stale {ChangeType} for {StreamId}: incoming v{In} < current v{Cur}",
                delivery.Message.ChangeType, StreamId, delivery.Message.Version, Current.Version);
            return;
        }
        if (acceptRebasedFull && Current is not null && delivery.Message.Version < Current.Version)
            logger.LogDebug(
                "[SYNC_STREAM] Accepting rebased resubscribe Full for {StreamId}: incoming v{In} < current v{Cur} (recycled owner re-snapshot)",
                StreamId, delivery.Message.Version, Current.Version);

        if (delivery.Message.ChangeType == ChangeType.Full)
        {
            logger.LogDebug("[SYNC_STREAM] Processing Full change for {StreamId}", StreamId);
            currentJson = JsonSerializer.Deserialize<JsonElement>(delivery.Message.Change.Content);
            try
            {
                // 🚨 Adopt the OWNER's Version (not local Host.Version) so the
                // monotonicity guard above compares apples-to-apples and a later
                // client write records the owner-version it was based on.
                // 🚨 A SUBSCRIBER's full-state proposal (a PatchDataChangeRequest a subscriber
                // sends as a Full because its own JSON cache was empty) carries the base it read,
                // not the owner's clock — floor it, exactly as in the Patch branch below, so
                // applying a subscriber write can never rewind this stream's version (#2701).
                SetCurrent(hub, new ChangeItem<TStream>(
                    currentJson.Value.Deserialize<TStream>(Host.JsonSerializerOptions)!,
                    StreamId,
                    isOwnerFrame || Current is null
                        ? delivery.Message.Version
                        : Math.Max(delivery.Message.Version, Current.Version)));
                Set(currentJson);
                // A Full re-established Current — any pending resync is satisfied;
                // allow a future Patch-before-Full gap to resubscribe again, and clear the
                // did-not-converge counter: THIS is the event that says the resync worked.
                ReleaseResyncGate();
                resyncAttempts = 0;
                // …and the evidence of non-convergence with it: a base snapshot ARRIVED, so every
                // ack that preceded it turned out to be answered after all (#1384). Interlocked
                // because the counter's other writer is the ack arm, on the response thread.
                Interlocked.Exchange(ref unansweredResyncs, 0);
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
                // RequestFreshSnapshot is gated by resyncInFlight — ONE re-ask
                // OUTSTANDING at a time, released when that re-ask is answered.
                //
                // 🚨 That last clause is the #2654 fix, and it is what makes this branch a recovery
                // rather than a trapdoor. The gate used to be released ONLY by a Full landing, so a
                // re-ask whose ANSWER never arrived — lost on the same leg that lost the frame,
                // refused, undeliverable — shut this branch permanently: every subsequent Patch
                // logged the Debug line below and was dropped, forever. The frame chain cannot see
                // that loss either, because a re-assert Full carries the version of the state it
                // re-asserts rather than a new one (BuildReassertFrame), so two consecutive frames
                // share a version and `BasedOnVersion` collapses — measured. The answer therefore
                // has to come from the REQUEST's own round trip, which is why RequestFreshSnapshot
                // now observes it.
                logger.LogDebug(
                    "[SYNC_STREAM] Patch before base Full for {StreamId}; requesting fresh snapshot", StreamId);
                RequestFreshSnapshot();
                return;
            }
            // 🚨 LOSS DETECTION (issue #1081). The owner chains consecutive frames: each
            // DataChangedEvent carries the Version of the frame SENT immediately before it
            // (JsonSynchronizationStream.ToDataChanged). The transport underneath (Orleans
            // memory streams) is at-most-once — a frame published before the subscriber's
            // stream subscription attached, or dropped under pressure, simply never arrives,
            // and NOTHING re-sends it. Before this check, that loss was invisible: later
            // patches kept applying cleanly on top (they touch other areas/entities), so the
            // mirror tracked the owner forever at a constant deficit — the measured shape of
            // the wedge: the owner pushed the compile-error-overlay Full (v6, the one frame
            // the test waits for), the client applied v1–v5 and then v7, v8; the page sat on
            // "awaiting first data" for the full 45s detector with no error anywhere. A Patch
            // whose BasedOnVersion is NOT the version we last applied proves the gap; the only
            // sound reaction is a fresh authoritative snapshot from the owner (event-driven,
            // storm-gated by resyncInFlight — no timer, no retry loop). Applies to
            // DataChangedEvent (owner→mirror) only: a PatchDataChangeRequest's chain is stamped
            // by the SENDING mirror and is not comparable to this stream's applied version.
            // Fulls need no gap check — a Full re-establishes the complete state, and a LOST
            // Full is caught here by the first Patch that chained onto it.
            if (delivery.Message is DataChangedEvent { BasedOnVersion: >= 0 } chained
                && chained.BasedOnVersion != Current.Version)
            {
                logger.LogWarning(
                    "[SYNC_STREAM] Frame loss detected for {StreamId}: incoming Patch v{In} chains onto v{Based} but the last applied frame is v{Cur} — a frame was lost in transport; requesting fresh snapshot from {Owner}",
                    StreamId, delivery.Message.Version, chained.BasedOnVersion, Current.Version, StreamIdentity.Owner);
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
                //
                // 🚨 …but ONLY for an owner frame (#2701). A SUBSCRIBER's write carries the base it
                // was computed on, which is BELOW this stream's clock exactly when the guard above
                // now lets it through. Adopting it would move the OWNER's Current BACKWARDS — and
                // the frame the owner then broadcasts would be stamped below what its other
                // subscribers already hold, so their (correct) monotonicity guards would drop it:
                // the same silent loss, one hop further out. The owner's clock is a FLOOR here, so
                // applying a subscriber's write can only ever move it forward.
                if (isOwnerFrame)
                    changeItem = changeItem with { Version = delivery.Message.Version };
                else if (changeItem.Version < Current!.Version)
                    changeItem = changeItem with { Version = Current.Version };

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
    // real requests (the TodoDataChangeWorkflow UpdateNodeRequest leak).
    //
    // 🚨 A GATE, NOT A TRAPDOOR (#2654). What it bounds is "ONE re-ask OUTSTANDING", and until now
    // it did not: it was released by exactly ONE event — a Full landing — while the re-ask itself
    // was posted FIRE-AND-FORGET down the very leg that had just proven it loses frames, with no
    // NACK arm, no OnError and no terminal of any kind. Every way the answer could fail to arrive
    // therefore shut this gate for the rest of the mirror's life: each later Patch took the "Patch
    // before base Full" branch, RequestFreshSnapshot no-opped on this flag, and the frame was
    // dropped at Debug. No error, no warning, no recovery — a layout area on its placeholder
    // forever, which is the whole of #2654.
    //
    // It is now released by the re-ask's ROUND TRIP, which is the event that actually bounds it:
    //   • the fresh Full lands — the mirror has its base (the success case, unchanged);
    //   • the owner ACKs — it has processed the re-subscribe and done whatever it is going to do,
    //     so the request is no longer outstanding;
    //   • the request is refused or unanswerable — see ResyncRefused.
    // Releasing the gate ASKS FOR NOTHING BY ITSELF: nothing here polls, retries or runs on a
    // timer. Only the next frame that PROVES the mirror still has no base drives a new re-ask, so
    // the rate is bounded by the round trip AND by the owner actually emitting — the same bound
    // JsonSynchronizationStream.Resubscribe's in-flight flag has always lived with, and the
    // opposite of the unbounded per-Patch flood this gate was introduced for.
    // Interlocked because the ack / refusal arms run on the response thread, not the hub turn.
    private int resyncInFlight;

    /// <summary>Re-opens the resync gate — see <see cref="resyncInFlight"/>. Never re-asks by
    /// itself: the next frame that proves the mirror still has no base does that.</summary>
    private void ReleaseResyncGate() => Interlocked.Exchange(ref resyncInFlight, 0);

    /// <summary>
    /// Consecutive fresh-snapshot requests that have not produced a base snapshot, reset by the
    /// Full that finally does. Anything above 1 is a mirror that asked and was NOT answered — the
    /// non-convergence of #2654 — which is what turns that state from silence into a Warning naming
    /// the stream and the owner. Hub-turn confined, like <see cref="Current"/>.
    /// </summary>
    private int resyncAttempts;

    /// <summary>
    /// Re-asks the owner ACKNOWLEDGED and then never followed with a base snapshot, reset by the
    /// <see cref="ChangeType.Full"/> that finally arrives. This — not <see cref="resyncAttempts"/> —
    /// is the evidence <see cref="MaxUnansweredResyncs"/> bounds, and the difference is the whole
    /// of the #2745 policy: a re-ask REFUSED transiently (a silo whose pod-hub claim has not landed
    /// during a rolling deploy answers <see cref="ErrorType.ShuttingDown"/>) is deliberately ridden
    /// out, so it must not accumulate towards a fault. Only an owner that said "I have processed
    /// your re-subscribe" and then produced nothing is evidence that the leg is eating this
    /// stream's snapshots.
    ///
    /// <para>Written from the response thread (the ack arm) and from the hub turn (the reset), so
    /// every access is interlocked.</para>
    /// </summary>
    private int unansweredResyncs;

    /// <summary>
    /// How many acknowledged-but-unanswered fresh-snapshot requests this mirror accumulates before
    /// it stops asking and FAULTS itself instead — the bound that turns #2654's residual silent
    /// forever-wedge into a surfaced error (Systemorph/MeshWeaver#1384).
    ///
    /// <para><b>What #2654 left behind.</b> The resync gate is released by the re-ask's ROUND TRIP
    /// (the Full, the owner's ack, or a verdict), so a lost fresh snapshot no longer latches the
    /// mirror shut: the next frame that proves the gap earns one new re-ask. That converges when
    /// the leg loses ONE frame. It does not terminate when the leg keeps losing this stream's
    /// Fulls — every re-ask is acked, every acked re-ask releases the gate, every answering Full
    /// dies on the same leg, and the mirror re-asks forever at Warning while its subscriber sits on
    /// "awaiting first data" with no error, no completion and nothing to re-establish from. That is
    /// the state measured on memex-cloud 2026-09-01 on <c>Event/SavGeneralversammlung2026/Talk</c>:
    /// <c>Frame loss detected … incoming Patch v13 chains onto v12 but the last applied frame is
    /// v11</c>, then a layout area <c>torn down having never rendered</c>. Recycling the pod holding
    /// the activation did not clear it, because nothing on the subscriber side had learned that
    /// anything had gone wrong.</para>
    ///
    /// <para><b>Why a COUNT is not a retry budget.</b> Nothing here retries and nothing polls: this
    /// counts EVIDENCE, not attempts at success. An increment costs a full round trip to the owner
    /// (the gate suppresses every re-ask while one is outstanding, and only the ANSWER releases it)
    /// PLUS a subsequent owner frame that proves the mirror still has no base. So
    /// <c>unansweredResyncs == n</c> means n separate authoritative snapshots were asked for,
    /// acknowledged, and never arrived. Raising this bound buys a longer silence, not a better
    /// chance — which is the opposite of a widened timeout, and why the number is small.</para>
    ///
    /// <para><b>Why 3.</b> The one healthy way to spend an attempt without converging is a Patch
    /// overtaking the answering Full — and the owner queues its re-assert on the SAME stream hub
    /// that produces the patches, so a patch can only overtake a re-assert that was already in
    /// flight when the re-ask landed. That is one redundant round trip, twice over at the very
    /// worst. Three consecutive acknowledged, unanswered asks is not that shape. The two failure
    /// directions are also not symmetric: faulting a stream that would eventually have converged
    /// costs one re-establish (<c>StreamLiveness.IsUsable</c> refuses to serve a faulted stream, so
    /// the workspace cache evicts it and the next natural caller opens a fresh one that subscribes
    /// from scratch), while NOT faulting costs a view that never loads and never says so.</para>
    /// </summary>
    private const int MaxUnansweredResyncs = 3;

    /// <summary>
    /// Set once this mirror has given up (see <see cref="MaxUnansweredResyncs"/>) so a later frame
    /// arriving on the already-faulted stream re-reports nothing. The store is terminal by then and
    /// would swallow a second <see cref="OnError"/> per the Rx grammar, but the Warning beside it
    /// would repeat per frame — log volume that says nothing new.
    /// </summary>
    private int resyncGaveUp;

    /// <summary>
    /// The re-ask currently outstanding. At most ONE at a time (<see cref="resyncInFlight"/>), so a
    /// <see cref="SerialDisposable"/> both bounds the registration — a CompositeDisposable entry per
    /// resync would grow for the life of the stream — and cancels a superseded pending callback,
    /// which is what keeps <c>responseSubjects</c> from accumulating entries nothing will answer.
    /// </summary>
    private readonly SerialDisposable resyncSubscription = new();

    /// <summary>
    /// Latch (0/1) set by <see cref="ExpectResubscribeFull"/> when a version-gated resubscribe is
    /// issued for a mirror that has DETECTED it is behind a (recycled) owner. The next
    /// <see cref="ChangeType.Full"/> received by <see cref="UpdateStream"/> consumes it and is
    /// accepted even if its FRAME version regressed below <see cref="Current"/> (a reactivated
    /// owner's <c>Hub.Version</c> resets to ~0) — the resubscribe-Current-reset for #325 symptom-2.
    /// </summary>
    private int _acceptResubscribeFull;

    /// <summary>
    /// Arms the mirror to ACCEPT the next authoritative <see cref="ChangeType.Full"/> even if its
    /// frame version has regressed below <see cref="Current"/>. Called by the sync-stream client's
    /// version-gated resubscribe (<c>JsonSynchronizationStream.CreateExternalClient</c>) the instant
    /// it decides the mirror is stale (the change feed announced a higher node version than the
    /// mirror holds) and posts a fresh <c>SubscribeRequest</c>. The owner's re-snapshot then lands
    /// instead of being dropped by the monotonicity guard, so a mirror orphaned by an idle-recycled
    /// owner converges (issue #325 symptom-2). Idempotent; consumed by the first Full.
    /// </summary>
    internal void ExpectResubscribeFull() => Interlocked.Exchange(ref _acceptResubscribeFull, 1);

    private void RequestFreshSnapshot()
    {
        // 🚨 Establish that we CAN ask before latching anything. A stream whose Reference is not a
        // WorkspaceReference has no owner-side subscription to refresh, and the previous order
        // (latch, null the cache, then discover there is nothing to post) closed the gate on a
        // re-ask that was never made — permanent by construction.
        if (Reference is not WorkspaceReference wsRef)
        {
            logger.LogDebug(
                "[SYNC_STREAM] Cannot request a fresh snapshot for {StreamId}: {Reference} is not a WorkspaceReference",
                StreamId, Reference);
            return;
        }
        // Terminal already declared below — this mirror has stopped asking for good. A later frame
        // must not re-enter: the stream is faulted, and re-reporting it every frame is log volume
        // that says nothing new.
        if (Volatile.Read(ref resyncGaveUp) == 1)
            return;
        if (Interlocked.Exchange(ref resyncInFlight, 1) == 1)
            return;

        // 🚨 …AND THE ONE THAT ENDS IT (#1384). VISIBLE is not the same as OVER: #2654 made the
        // non-convergence audible in a portal log and left the SUBSCRIBER exactly where it was —
        // holding a placeholder for a snapshot that is not coming, with no error and no completion,
        // so nothing downstream could re-establish. A log line is not an API. Past
        // MaxUnansweredResyncs acknowledged-and-unanswered asks this mirror stops asking and FAULTS
        // instead, which is the one signal a subscriber can act on: the store's terminal error
        // reaches every reader, StreamLiveness.IsUsable stops serving this stream from the
        // workspace caches, and the next natural caller opens a fresh one.
        //
        // Read AFTER the gate is taken, so "no ask is outstanding" is part of the verdict — an
        // answer still in flight is not evidence of anything. The gate is then released BEFORE the
        // fault, in the order this method's opening comment teaches: never leave a latch behind a
        // decision that posted no request. Releasing it asks for nothing by itself, and resyncGaveUp
        // keeps a later frame from re-reporting a stream that is already terminal.
        //
        // 🚨 A MIRROR ONLY, and the qualifier is load-bearing. PatchDataChangeRequest reaches
        // UpdateStream on the OWNER's server-side stream too, where a subscriber's write arriving
        // before that stream has emitted its first frame (its outbound JSON cursor still null)
        // takes the same "Patch before base Full" branch. On such a stream the count measures
        // nothing: the re-ask is addressed to this very hub, and the Full that would reset it is
        // one this stream SENDS rather than receives — so it could only ever climb, and faulting on
        // it would kill an owner's stream for a subscriber-side race. Convergence is a claim about
        // a REMOTE owner; only a stream that has one can fail to converge. Same predicate as
        // OwnerVersion() uses to tell the two apart.
        //
        // Read ONCE into a local, and report from that local everywhere. The verdict, the Warning and
        // the exception must be the same number: a diagnostic that says "gave up after 3" beside an
        // exception that says "after 4" costs the next reader more than it tells them, and a second
        // read is one more thing they would have to prove is stable. (It is — the gate is HELD from
        // here, so the ack arm cannot increment, and the reset runs on this same hub turn — but the
        // local means nobody has to reconstruct that argument to trust the number.)
        var unanswered = Volatile.Read(ref unansweredResyncs);
        if (unanswered >= MaxUnansweredResyncs && !Owner.Equals(Host.Address))
        {
            Volatile.Write(ref resyncGaveUp, 1);
            ReleaseResyncGate();
            logger.LogWarning(
                "[SYNC_STREAM] Resync gave up for {StreamId}: {Attempts} consecutive fresh-snapshot requests to {Owner} were acknowledged and none produced a base snapshot — faulting the mirror so its subscribers can re-establish",
                StreamId, unanswered, Owner);
            OnError(new StreamNotConvergingException(
                $"Synchronization stream '{StreamId}' could not recover from a lost frame: "
                + $"{unanswered} consecutive fresh-snapshot requests to owner '{Owner}' were "
                + "acknowledged and none produced a base snapshot. The mirror holds no state it can "
                + "patch onto, so it is faulted rather than left waiting for a snapshot that is not "
                + "arriving."));
            return;
        }

        // 🚨 THE ONE LINE THAT MAKES NON-CONVERGENCE VISIBLE. A second consecutive ask means the
        // first one produced no base snapshot — the answer was lost, refused or thrown away — which
        // is exactly the state #2654 spent its whole life in at Debug level, with a stuck layout
        // area as the only outward sign. Warning names the stream and the owner so a portal log
        // distinguishes "gaps that were answered" (the healthy shape, §6 of DataSyncAndCrdt) from
        // "a stream that keeps asking and never converges" (the defect).
        if (++resyncAttempts > 1)
            logger.LogWarning(
                "[SYNC_STREAM] Resync has not converged for {StreamId}: asking {Owner} for a fresh snapshot again (attempt {Attempt}) — the previous request produced no base snapshot",
                StreamId, StreamIdentity.Owner, resyncAttempts);
        Set<JsonElement?>(null);
        var accessService = Host.ServiceProvider
            .GetService(typeof(AccessService)) as AccessService;
        using (accessService?.ImpersonateAsSystem())
        {
            // 🚨 Minted, never constructed inline: a re-subscribe declares the SAME negotiated
            // wire capabilities as the initial subscribe. When the owner cannot match this
            // request to a live server-side stream (the subscriber was evicted on a
            // TargetUnserved verdict, the owner recycled) it builds a fresh one and reads the
            // capabilities off THIS request — so a capability omitted here is withdrawn for the
            // rest of the mirror's life, silently. See MintSubscribeRequest.
            //
            // 🚨 OBSERVE, never Post (#2654). This is the THIRD SubscribeRequest site, and until
            // now it was the only one with no arm on its answer — the other two
            // (CreateExternalClient's initial subscribe and its Resubscribe) have always used
            // hub.Observe precisely so a refusal reaches them. SubscribeRequest is an
            // IRequest<SubscribeAck> and the owner answers every one of them
            // (DataExtensions.HandleSubscribeRequest), so a verdict — ack, DeliveryFailure, or the
            // hub's own "no response" terminal — always arrives. Fire-and-forget threw all three
            // away and left the gate shut on silence.
            //
            // 🚨 DEFER, so a SYNCHRONOUS throw is an OnError. Host.Observe posts inline (it
            // registers the response subject and then calls Post), and a Post that throws — the
            // host is disposing, routing refuses — would otherwise bubble out of UpdateStream with
            // the gate already set and the cached JSON already dropped: the exact permanent wedge
            // this change exists to remove, recreated on the failure path. Inside Defer the throw
            // reaches ResyncRefused like any other verdict, which always releases the gate. The
            // factory still runs inside the impersonation scope, because Subscribe is called here.
            resyncSubscription.Disposable = Observable
                .Defer(() => Host.Observe(
                    JsonSynchronizationStream.MintSubscribeRequest(StreamId, wsRef, identity: null)
                        with { Subscriber = Configuration.Subscriber! },
                    o => o.WithTarget(StreamIdentity.Owner)))
                .Take(1)
                .Subscribe(
                    _ =>
                    {
                        // 🚨 THE ACK RELEASES THE GATE. It means the owner has PROCESSED this
                        // re-subscribe and done whatever it is going to do about it, so the request
                        // is no longer outstanding — and the gate's contract is "one OUTSTANDING
                        // re-ask", never "one re-ask ever". Waiting for the Full instead is what
                        // made the gate permanent whenever the Full did not arrive, and the frame
                        // chain cannot cover for that: a re-assert Full carries the version of the
                        // state it re-asserts (BuildReassertFrame), so it shares a version with the
                        // frame before it and its loss leaves the BasedOnVersion chain looking
                        // intact — measured on StreamResyncConvergenceTest.
                        //
                        // Releasing here re-asks NOTHING. The next re-ask needs a frame that proves
                        // the mirror still has no base, so the worst case is one redundant round
                        // trip when a Patch overtakes the answering Full — and that redundant
                        // re-ask is itself answered with a Full, which ends the cycle.
                        //
                        // 🚨 …AND THE ACK IS ALSO THE EVIDENCE (#1384). An acknowledgement says the
                        // owner processed this re-subscribe; if a base snapshot never follows, the
                        // answer died on the leg rather than never being sent, which is exactly the
                        // non-convergence MaxUnansweredResyncs bounds. Counted HERE and nowhere
                        // else, deliberately: a re-ask REFUSED transiently is ridden out (#2745)
                        // and must not accumulate towards a fault, and an increment that races an
                        // answering Full is undone by that Full's reset a moment later.
                        logger.LogDebug(
                            "[SYNC_STREAM] Fresh-snapshot request for {StreamId} acknowledged by owner {Owner}",
                            StreamId, StreamIdentity.Owner);
                        Interlocked.Increment(ref unansweredResyncs);
                        ReleaseResyncGate();
                    },
                    ResyncRefused);
        }
    }

    /// <summary>
    /// The failure arm the fresh-snapshot re-ask never had (#2654). Called when the owner answers
    /// the re-ask with anything other than a <see cref="SubscribeAck"/> — a
    /// <see cref="DeliveryFailure"/>, or the hub's own "no response" terminal when the request was
    /// undeliverable.
    ///
    /// <para>It re-opens the gate FIRST and unconditionally: whatever the verdict, this re-ask is
    /// over, and keeping the gate shut on a dead re-ask is what made non-convergence permanent and
    /// silent. Re-opening asks for nothing by itself — the next frame that proves the mirror is
    /// still without a base drives exactly one new re-ask, so a mirror whose owner has gone quiet
    /// stays quiet too.</para>
    ///
    /// <para>The classification is the SAME ONE this stream's own <c>DeliveryFailure</c> handler
    /// applies, deliberately — one policy per type, not two:
    /// <see cref="ErrorType.ShuttingDown"/> is transient and gets ridden out; every other verdict is
    /// terminal and faults the stream, so the subscriber SEES a failure instead of holding a
    /// placeholder for a snapshot that is never coming.</para>
    ///
    /// <para>🚨 It keys on <see cref="ErrorType"/> and NEVER on
    /// <see cref="DeliveryFailure.TargetUnserved"/>. That stamp is the OWNER-side eviction gate
    /// (<c>DataExtensions.HandleTargetUnservedFailure</c>, #2426/#2546), and the router deliberately
    /// stamps it on BOTH of its "nobody serves that address" verdicts — the terminal
    /// no-live-subscriber refusal (<c>RefuseNoSubscriber</c>, <see cref="ErrorType.NotFound"/>) and
    /// the TRANSIENT pod-hub refusal a rolling deploy produces while a silo's claim has not landed
    /// yet (<c>AnswerPodHubNotHere</c>, <see cref="ErrorType.ShuttingDown"/>, #2745). Reading the
    /// stamp as "terminal" would fault every mirror in that overlap window; RoutingGrain's own
    /// contract says it: the stamp is the eviction gate, the ErrorType beside it says whether the
    /// SENDER keeps its recovery armed, and the two are independent.</para>
    ///
    /// <para>A verdict that never arrives at all — the request was undeliverable, so the hub's own
    /// request/response terminal fires — is neither: it is reported at Warning and left recoverable,
    /// because "we could not find out" is not an answer about the owner.</para>
    /// </summary>
    private void ResyncRefused(Exception error)
    {
        ReleaseResyncGate();

        if (error is DeliveryFailureException { Failure: { } failure })
        {
            if (failure.ErrorType == ErrorType.ShuttingDown)
            {
                logger.LogDebug(
                    "[SYNC_STREAM] Fresh-snapshot request for {StreamId} was rejected by {Owner} while it is shutting down or not yet served (TargetUnserved={TargetUnserved}) — keeping the stream alive; the next proven gap re-asks",
                    StreamId, StreamIdentity.Owner, failure.TargetUnserved);
                return;
            }
            logger.LogWarning(
                "[SYNC_STREAM] Fresh-snapshot request for {StreamId} was refused terminally by {Owner} ({ErrorType}, TargetUnserved={TargetUnserved}): {Message}",
                StreamId, StreamIdentity.Owner, failure.ErrorType, failure.TargetUnserved,
                failure.Message);
            OnError(error);
            return;
        }

        // Teardown is not a fault to report: a re-ask racing this stream's own disposal cannot be
        // answered and does not need to be. Same classification OnError applies, for the same
        // reason — a Warning per disposed stream is log volume, not information.
        if (IsObjectDisposed(error))
        {
            logger.LogDebug(error,
                "[SYNC_STREAM] Fresh-snapshot request for {StreamId} could not be issued — the stream or its host is tearing down",
                StreamId);
            return;
        }

        logger.LogWarning(error,
            "[SYNC_STREAM] Fresh-snapshot request for {StreamId} was not answered by {Owner} — the mirror has no base snapshot; the next frame that proves the gap will re-ask",
            StreamId, StreamIdentity.Owner);
    }



    /// <summary>
    /// Per-instance unique identifier of this stream object (a fresh GUID), distinct from <see cref="ClientId"/>.
    /// </summary>
    public string StreamId { get; } = Guid.NewGuid().AsString();
    /// <summary>
    /// The stable client/stream identity used for echo suppression and change authorship
    /// (the value set via <see cref="StreamConfiguration{TStream}.WithClientId"/>).
    /// </summary>
    public string ClientId => Configuration.ClientId;
    /// <summary>
    /// Optional identity tag for the stream, or <c>null</c> when unset.
    /// </summary>
    public string? Identity { get; init; }


    internal StreamConfiguration<TStream> Configuration { get; }

    /// <summary>
    /// 🚨 REFERENCE identity — deliberately NOT the record-synthesized structural equality.
    /// <para>
    /// A stream is a LIVE object, not a value: it owns a <see cref="ReplaySubject{T}"/>, mutable
    /// <c>current</c> state, a hosted hub, subscriptions and disposal state. Two distinct stream
    /// instances are never "the same stream", so reference identity IS the correct semantics
    /// (the synthesized version already behaved this way in practice — each instance carries its
    /// own <see cref="Store"/> subject, compared by reference).
    /// </para>
    /// <para>
    /// It is also the only SAFE semantics. The synthesized <c>GetHashCode</c>/<c>Equals</c> walk
    /// every instance field, including <see cref="Configuration"/> — and
    /// <see cref="StreamConfiguration{TStream}.Stream"/> points straight back here. That closed a
    /// cycle <c>SynchronizationStream.GetHashCode → StreamConfiguration.GetHashCode →
    /// EqualityComparer&lt;ISynchronizationStream&lt;TStream&gt;&gt;.Default.GetHashCode(Stream) →
    /// …</c> with no base case, so ANY hash of a stream instance (a dictionary key, a log scope,
    /// a HashSet) recursed until the process died on an uncatchable StackOverflowException
    /// (#2163/#2164/#2172/#2173/#2174/#2175 — repeated pod kills in prod).
    /// </para>
    /// </summary>
    /// <param name="other">The stream to compare against.</param>
    /// <returns>True only when <paramref name="other"/> is this very instance.</returns>
    public virtual bool Equals(SynchronizationStream<TStream>? other) => ReferenceEquals(this, other);

    /// <summary>
    /// Reference-identity hash — see <see cref="Equals(SynchronizationStream{TStream})"/> for why
    /// structural hashing of a stream is both wrong and fatal.
    /// </summary>
    /// <returns>The runtime identity hash of this instance.</returns>
    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);


    /// <summary>
    /// Disposes the stream: completes the underlying store and tears down the hosted hub.
    /// Idempotent.
    /// </summary>
    public void Dispose()
    {
        lock (disposeLock)
        {
            if (isDisposed)
                return;
            isDisposed = true;
        }
        // COMPLETE the store — deliberately do NOT dispose it (#1170/#1171). OnCompleted
        // detaches every subscriber, and per the Rx grammar a completed subject silently
        // ignores any further OnNext/OnError/OnCompleted — exactly the recognized-shutdown
        // outcome an in-flight teardown delivery needs. Disposing the subject instead made
        // it THROW: during MessageHub shutdown, sync hubs dispose in parallel (each disposal
        // action on its own action block), and one stream's completion (this very line, on
        // thread 1) drains through the Synchronize chain wired by CreateReducedStream
        // (WorkspaceStreams.cs, `.Subscribe(reducedStream)`) into a sibling stream's
        // OnCompleted while that sibling's own Dispose runs on thread 2. The sibling's
        // `!Store.IsDisposed` pre-check was check-then-act, so a Store.Dispose() landing
        // inside the window turned the benign completion into the ObjectDisposedException
        // logged as "Error during shutdown of hub sync/…" / "Hub sync/… disposal faulted".
        // Nothing is leaked by not disposing: completion drops all observer references, and
        // the 1-item replay buffer is equally reachable via `current` for the stream's
        // remaining lifetime.
        Store.OnCompleted();
        // The shared children were registered for disposal on THIS stream (CreateReducedStream),
        // so the composite below is what disposes them; dropping the index here just stops this
        // corpse from referencing them.
        sharedReduceCache.Clear();

        // 🚨 SYNCHRONOUSLY, and BEFORE Hub.Dispose() — this is the whole point of the stream owning
        // its own composite (#1613). The registrant that matters most is the hub.Observe
        // subscription for the initial SubscribeRequest: disposing it is what removes the pending
        // callback from responseSubjects. Routed through the hub's ShutDown phase instead, that
        // removal happened several action-block turns after the caller had already gone away — so a
        // subscribe the owner never answered (because the owner was still Starting and the delivery
        // sat DEFERRED behind its init gates) stayed pending until the ~30 s RequestTimeout.
        //
        // Guarded: Dispose() is called from consumers' `.Finally(stream.Dispose)`, and a throw out
        // of an Rx Finally REPLACES the terminal notification. A faulting registrant must be
        // reported, never allowed to swap a completion for an error — and Hub.Dispose() below still
        // has to run.
        try
        {
            streamDisposables.Dispose();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "[SYNC_STREAM] Registrant faulted while disposing stream {StreamId}", StreamId);
        }

        if (Hub is not null && Hub.RunLevel <= MessageHubRunLevel.Started)
            Hub.Dispose();
    }
    private ConcurrentDictionary<string, object?> Properties { get; } = new();
    /// <summary>Reads a property bag value by key.</summary>
    /// <typeparam name="T">Expected value type.</typeparam>
    /// <param name="key">The property key.</param>
    /// <returns>The stored value cast to <typeparamref name="T"/>, or the default if absent.</returns>
    public T? Get<T>(string key) => (T?)Properties.GetValueOrDefault(key);
    /// <summary>Reads a property bag value keyed by the full name of <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">Both the value type and the implicit key.</typeparam>
    /// <returns>The stored value, or the default if absent.</returns>
    public T? Get<T>() => Get<T>(typeof(T).FullName!);
    /// <summary>Stores a property bag value under the given key.</summary>
    /// <typeparam name="T">Value type.</typeparam>
    /// <param name="key">The property key.</param>
    /// <param name="value">The value to store.</param>
    public void Set<T>(string key, T? value) => Properties[key] = value;
    /// <summary>Stores a property bag value keyed by the full name of <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">Both the value type and the implicit key.</typeparam>
    /// <param name="value">The value to store.</param>
    public void Set<T>(T? value) => Properties[typeof(T).FullName!] = value;

    private readonly ConcurrentDictionary<int, Task> tasks = new();
    /// <summary>
    /// Ties a background task's lifetime to the stream: tracks it until completion and faults the stream
    /// if the task faults.
    /// </summary>
    /// <param name="task">The task to bind.</param>
    public void BindToTask(Task task)
    {
        tasks[task.Id] = task;
        task.ContinueWith(t =>
        {
            tasks.TryRemove(task.Id, out var _);
            if (t is { IsFaulted: true, Exception: not null })
                FaultStore(t.Exception);
        });
    }

    /// <summary>
    /// Internal hub message carrying a pending value transform and its error callback to the stream's
    /// hub, where it is applied serially.
    /// </summary>
    /// <param name="Update">The transform mapping the current state to the change to apply.</param>
    /// <param name="ExceptionCallback">Invoked synchronously if applying the update throws.</param>
    /// <param name="Applied">Optional: invoked in the SAME turn, right after the change was applied
    /// (also for a no-op transform). This is the stream's completion seam — a writer that must report
    /// "committed" uses it instead of scheduling a second turn, so reporting costs no extra message
    /// and can never observe the pre-change state.</param>
    [PreventLogging]
    public record UpdateStreamRequest([property: JsonIgnore] Func<TStream?, ChangeItem<TStream>?> Update, [property: JsonIgnore] Action<Exception> ExceptionCallback, [property: JsonIgnore] Action? Applied = null);

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


/// <summary>
/// Immutable configuration for a <see cref="SynchronizationStream{TStream}"/>: client identity,
/// subscriber address, initialization strategy, infrastructure flag, and exception handling.
/// Builder methods return modified copies.
/// </summary>
/// <typeparam name="TStream">Type of the state carried by the stream being configured.</typeparam>
/// <param name="Stream">The stream this configuration belongs to.</param>
public record StreamConfiguration<TStream>(ISynchronizationStream<TStream> Stream)
{
    internal string ClientId { get; init; } = Guid.NewGuid().AsString();
    /// <summary>
    /// Sets the stable client/stream identity used for change authorship and echo suppression.
    /// </summary>
    /// <param name="streamId">The client/stream identity to use.</param>
    /// <returns>A copy with the client id set.</returns>
    public StreamConfiguration<TStream> WithClientId(string streamId) =>
        this with { ClientId = streamId };

    /// <summary>
    /// The address of the subscriber (client/portal) that subscribed to this stream.
    /// Used for sending messages back to the subscriber, such as NavigationRequest.
    /// </summary>
    public Address? Subscriber { get; init; }
    /// <summary>
    /// Sets the subscriber (client/portal) address that messages can be routed back to.
    /// </summary>
    /// <param name="subscriber">The subscriber address.</param>
    /// <returns>A copy with the subscriber set.</returns>
    public StreamConfiguration<TStream> WithSubscriber(Address subscriber) =>
        this with { Subscriber = subscriber };

    internal bool NullReturn { get; init; }

    /// <summary>
    /// Configures the stream to return <c>null</c> (rather than throwing or waiting) when the referenced
    /// state is not present.
    /// </summary>
    /// <returns>A copy with null-when-absent behaviour enabled.</returns>
    public StreamConfiguration<TStream> ReturnNullWhenNotPresent()
        => this with { NullReturn = true };

    /// <summary>
    /// Marks this stream as a genuine INFRASTRUCTURE mirror (a data-source <see cref="EntityStore"/>
    /// store: <c>ds/Activity</c>, <c>ds/&lt;partition&gt;</c>, …). When set, a
    /// <see cref="SynchronizationStream{TStream}.Update(System.Func{TStream,ChangeItem{TStream}},System.Action{System.Exception})"/>
    /// whose live AsyncLocal context is gone AND for which no creation / owner identity can be
    /// resolved stamps the well-known System identity on the resulting <c>UpdateStreamRequest</c>
    /// instead of posting context-less and being failed closed by the never-null PostPipeline guard
    /// (which would terminally fault the Store and poison every future subscriber). Identical rule to
    /// <c>DataSourceWithStorage</c>'s persistence-as-System and <c>VirtualDataSource</c>'s mirror
    /// writes; real-user writes are unaffected (a live identity always wins).
    /// </summary>
    internal bool RunsAsInfrastructure { get; init; }

    /// <inheritdoc cref="RunsAsInfrastructure"/>
    public StreamConfiguration<TStream> AsInfrastructure(bool value = true)
        => this with { RunsAsInfrastructure = value };

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

    /// <summary>
    /// Sets a one-shot, Task-based initializer producing the stream's initial state. The Task is bridged
    /// reactively at the stream's hub-init boundary; its single result becomes the initial current value.
    /// Mutually exclusive with the observable-based <c>WithInitialization</c> overload.
    /// </summary>
    /// <param name="init">Produces the initial state for the stream.</param>
    /// <returns>A copy with the initializer set.</returns>
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

    /// <summary>
    /// Sets the callback invoked when an exception occurs on the stream.
    /// </summary>
    /// <param name="exceptionCallback">The exception handler.</param>
    /// <returns>A copy with the exception callback set.</returns>
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

    /// <summary>
    /// 🚨 Compares this configuration's OWN values; the <see cref="Stream"/> back-pointer
    /// participates BY REFERENCE and is never traversed structurally.
    /// <para>
    /// <see cref="Stream"/> points back at the stream that OWNS this configuration
    /// (<c>new StreamConfiguration&lt;TStream&gt;(this)</c>), so letting the record-synthesized
    /// members walk it closes an unbounded cycle through
    /// <see cref="SynchronizationStream{TStream}"/> — the StackOverflow that killed portal pods
    /// (#2163/#2164/#2172/#2173/#2174/#2175). The owning stream is part of this configuration's
    /// IDENTITY, not of its value, which is exactly what reference comparison expresses.
    /// </para>
    /// </summary>
    /// <param name="other">The configuration to compare against.</param>
    /// <returns>True if both configurations belong to the same stream and carry the same settings.</returns>
    public virtual bool Equals(StreamConfiguration<TStream>? other) =>
        other is not null
        && (ReferenceEquals(this, other)
            || (ReferenceEquals(Stream, other.Stream)
                && ClientId == other.ClientId
                && Equals(Subscriber, other.Subscriber)
                && NullReturn == other.NullReturn
                && RunsAsInfrastructure == other.RunsAsInfrastructure
                && DeferredInitialization == other.DeferredInitialization
                && DeferredGateName == other.DeferredGateName
                && Equals(Initialization, other.Initialization)
                && Equals(ObservableInitialization, other.ObservableInitialization)
                && Equals(ExceptionCallback, other.ExceptionCallback)
                && Equals(DeferredGatePredicate, other.DeferredGatePredicate)));

    /// <summary>
    /// Hash over this configuration's own settings plus the owning stream's REFERENCE identity —
    /// never a structural walk of <see cref="Stream"/>. See
    /// <see cref="Equals(StreamConfiguration{TStream})"/>. The delegate-valued settings are
    /// deliberately omitted (equal configurations still hash equal, which is all the contract asks).
    /// </summary>
    /// <returns>A bounded, cycle-free hash code.</returns>
    public override int GetHashCode() =>
        HashCode.Combine(
            RuntimeHelpers.GetHashCode(Stream),
            ClientId,
            Subscriber,
            NullReturn,
            RunsAsInfrastructure,
            DeferredInitialization,
            DeferredGateName);
}
