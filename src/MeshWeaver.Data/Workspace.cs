using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Messaging;
using MeshWeaver.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// The shared mesh-node cache (MeshNodeStreamHandle in MeshWeaver.Mesh.Contract) and the
// MeshNode reduce-callback plumbing (MeshDataSource / SyncedQueryDataSourceExtensions in
// MeshWeaver.Graph) are the ONLY sanctioned callers of the raw single-node remote reduce.
// They open their upstream via the internal GetRemoteStreamUnchecked escape hatch so the
// public GetRemoteStream<MeshNode> path can log its discouraged-usage warning for everyone
// else without spamming it on the sanctioned hot paths.
[assembly: InternalsVisibleTo("MeshWeaver.Mesh.Contract")]
[assembly: InternalsVisibleTo("MeshWeaver.Graph")]
// Framework-internal tests of the raw remote-stream cache identity
// (ReferenceEquals on Workspace._remoteStreamCache) legitimately open the raw
// single-node reduce — they test the mechanism itself, not mesh-node access.
[assembly: InternalsVisibleTo("MeshWeaver.Query.Test")]
// Owner-side merge internals (ApplyMeshNodeMerge / RebaseMonotonicTriggers): the
// monotonic-trigger merge semantics are pinned by deterministic unit tests
// (MonotonicTriggerMergeTest) against the exact internal seam the
// PatchDataRequest handler runs — not a re-implementation of the call sequence.
[assembly: InternalsVisibleTo("MeshWeaver.Data.Test")]

namespace MeshWeaver.Data;

/// <summary>
/// Default <see cref="IWorkspace"/> implementation: builds the data context from the hub's
/// configuration, caches remote synchronization streams (evicting them when their owner node
/// changes), and routes reads, writes and disposal through the owning message hub.
/// </summary>
public class Workspace : IWorkspace
{
    private readonly ILogger<Workspace> _logger;
    private readonly IDisposable? _changeFeedSubscription;

    // Resolved once: GetExternalClientSynchronizationStream reads the ambient identity on every
    // call — including every cache HIT (a Blazor re-render re-binding an area) — so this must not
    // be a per-call service-provider lookup. The AccessService itself is a singleton whose
    // Context/CircuitContext are AsyncLocal, so holding the instance still reads the CURRENT
    // caller's identity; it is the resolution that is cached, never the identity.
    private readonly AccessService? _accessService;

    /// <summary>Creates the workspace, builds and initializes its data context, and subscribes to the mesh change feed for remote-stream cache eviction.</summary>
    /// <param name="hub">The message hub that owns this workspace.</param>
    /// <param name="logger">Logger for workspace lifecycle and stream-cache diagnostics.</param>
    public Workspace(IMessageHub hub, ILogger<Workspace> logger)
    {
        Hub = hub;
        _logger = logger;
        _accessService = hub.ServiceProvider.GetService<AccessService>();
        logger.LogDebug("Creating data context of address {address}", Id);
        DataContext = this.CreateDataContext();
        logger.LogDebug("Started initialization of data context of address {address}", Id);
        DataContext.Initialize();

        // Evict cached remote streams when their owner node changes (delete, recreate,
        // recycle, content/type update). Without this, a Singleton workspace keeps
        // serving the original snapshot forever — including across Blazor circuit
        // refreshes, since the workspace lives on the singleton mesh hub. The next
        // GetRemoteStream after eviction creates a fresh subscription against the
        // (re-)activated owner and pulls the current persistence state.
        //
        // IMeshChangeFeed lives in MeshWeaver.Mesh.Contract which would create a
        // Data → Mesh.Contract → Layout → Data project cycle. Resolve via reflection
        // and adapt the Subscribe(Action<MeshChangeEvent>, MeshChangeKind?) signature.
        _changeFeedSubscription = TrySubscribeToChangeFeed(hub.ServiceProvider, _logger,
            evtPath => EvictForPath(evtPath));
    }

    private static IDisposable? TrySubscribeToChangeFeed(
        IServiceProvider serviceProvider, ILogger logger, Action<string> onPathChanged)
    {
        try
        {
            var feedType = Type.GetType("MeshWeaver.Mesh.Services.IMeshChangeFeed, MeshWeaver.Mesh.Contract", throwOnError: false);
            if (feedType is null) return null;
            var feed = serviceProvider.GetService(feedType);
            if (feed is null) return null;

            var eventType = Type.GetType("MeshWeaver.Mesh.Services.MeshChangeEvent, MeshWeaver.Mesh.Contract", throwOnError: false);
            if (eventType is null) return null;
            var pathProp = eventType.GetProperty("Path");
            if (pathProp is null) return null;

            // Build a strongly-typed Action<MeshChangeEvent> via a generic helper so the
            // runtime sees the exact delegate signature Subscribe expects.
            var helper = typeof(Workspace).GetMethod(nameof(SubscribeChangeFeedHelper),
                BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(eventType);
            return (IDisposable?)helper.Invoke(null, [feed, pathProp, onPathChanged]);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Workspace failed to subscribe to IMeshChangeFeed — remote stream cache will only invalidate via heartbeat resubscribe.");
            return null;
        }
    }

    private static IDisposable? SubscribeChangeFeedHelper<TEvent>(
        object feed, PropertyInfo pathProperty, Action<string> onPathChanged)
        where TEvent : class
    {
        Action<TEvent> handler = evt =>
        {
            try
            {
                if (pathProperty.GetValue(evt) is string p && !string.IsNullOrEmpty(p))
                    onPathChanged(p);
            }
            catch { /* keep change-feed alive on handler faults */ }
        };
        var subscribe = feed.GetType().GetMethod("Subscribe");
        return (IDisposable?)subscribe!.Invoke(feed, [handler, null]);
    }

    /// <summary>
    /// Drops any cached remote streams whose owner address matches the changed path.
    /// The currently-attached subscribers stay live (they continue to receive
    /// DataChanged events from the source hub for the moment); the eviction only
    /// affects the NEXT GetRemoteStream caller, which will spin up a fresh stream.
    /// </summary>
    private void EvictForPath(string path)
    {
        if (string.IsNullOrEmpty(path) || _remoteStreamCache.IsEmpty)
            return;

        // Do NOT dispose the evicted stream — existing subscribers (e.g. live Blazor
        // components) are still attached to it and need to keep receiving updates
        // until they drop on their own. The eviction only prevents NEW callers from
        // re-using the now-stale stream; the next GetRemoteStream creates a fresh one
        // against the (re-)activated owner.
        foreach (var key in _remoteStreamCache.Keys)
        {
            // Owner-address match only — every identity's stream for that owner is evicted.
            if (string.Equals(key.Owner.ToString(), path, StringComparison.OrdinalIgnoreCase)
                && _remoteStreamCache.TryRemove(key, out var removed))
            {
                // Keep ownership of the evicted stream so Dispose still tears
                // down its `sync/` hub — dropping it here orphaned the hub (never
                // disposed → TimerQueue-pinned forever). Only a materialised stream
                // has a hub to dispose.
                if (removed.IsValueCreated)
                    _evictedRemoteStreams[removed.Value] = 0;
                _logger.LogDebug(
                    "Evicted remote stream cache for {Address} after change event.",
                    key.Owner);
            }
        }
    }

    /// <summary>
    /// Opens the initialization gate after all handlers are registered.
    /// Called via SyncBuildupActions to ensure proper ordering.
    /// </summary>
    internal void OpenInitializationGate()
    {
        DataContext.OpenInitializationGate();
    }


    /// <inheritdoc />
    public IReadOnlyCollection<Type> MappedTypes => DataContext.MappedTypes.ToArray();


    /// <inheritdoc />
    public IObservable<IEnumerable<TType>>? GetRemoteStream<TType>(Address address)
    {
        ThrowIfMeshNode(typeof(TType));
        return GetRemoteStream(
            address,
            new CollectionReference(Hub.TypeRegistry.GetOrAddType(typeof(TType), typeof(TType).Name))
            )?.Select(x => x.Value!.Instances.Values.OfType<TType>());
    }

    // 🚨 GetRemoteStream<MeshNode> is DISCOURAGED — the single-node remote reduce does not
    // converge well (divergent mirror streams, writes invisible to readers). The single
    // canonical API for a mesh node by path is workspace.GetMeshNodeStream(path) /
    // hub.GetMeshNodeStream(path), which routes every reader and writer through the shared
    // IMeshNodeStreamCache. We THROW so every callsite is caught and migrated — the
    // single-node remote reduce does not converge, so any direct use is a latent bug.
    // MeshWeaver.Data cannot reference the MeshNode type (it lives downstream in
    // MeshWeaver.Mesh.Contract), so detect it by name. The cache + reduce-callback plumbing
    // are the ONLY sanctioned openers and bypass this guard via the internal
    // GetRemoteStreamUnchecked overloads below.
    private static void ThrowIfMeshNode(Type reducedType)
    {
        if (reducedType.Name == "MeshNode")
            throw new InvalidOperationException(
                "GetRemoteStream<MeshNode> is forbidden — the single-node remote reduce does not converge. "
                + "Use workspace.GetMeshNodeStream(path) / hub.GetMeshNodeStream(path), which routes through the "
                + "shared mesh-node cache (IMeshNodeStreamCache). Framework internals open the raw stream via the "
                + "sanctioned GetRemoteStreamUnchecked escape hatch.");
    }

    /// <inheritdoc />
    public IObservable<T[]?>? GetStream<T>()
    {
        // 🚨 EXACT type source, never the base-walking DataContext.GetTypeSource(Type).
        // That walk is a WRITE-path affordance — WorkspaceOperations.ClassifyForRouting stores a
        // derived instance into its base's collection, and ImportManager falls back to the base
        // explicitly. Used as the READ guard it admitted a type that owns NO collection of its
        // own, and the very next line resolves the collection name through TypeRegistry, which
        // does NOT walk: the mismatch threw "Type X is unknown." straight out of the caller's
        // layout-area render ("Rendering failed for area X") instead of returning the documented
        // null that every caller already handles with `?? Observable.Return(...)`.
        if (DataContext.GetCollectionName(typeof(T)) == null)
            return null;
        // Hub already past Started → SynchronizationStream..ctor would throw
        // ObjectDisposedException synchronously and the exception would
        // propagate as a DeliveryFailure for any layout-area handler currently
        // composing menu items / etc. against this workspace. Match the existing
        // "return null" contract for unknown collections; callers (e.g.
        // NodeMenuItemsExtensions.GetMenuContext) already handle null with
        // `?? Observable.Return(empty)`.
        if (Hub.RunLevel > MessageHubRunLevel.Started)
            return null;
        return GetStream(typeof(T))
            .Synchronize()
            .Select(x => x.Value?.Collections.SingleOrDefault().Value?.Instances.Values.Cast<T>().ToArray());
    }

    /// <inheritdoc />
    public ISynchronizationStream<TReduced> GetRemoteStream<TReduced>(
        Address id,
        WorkspaceReference<TReduced> reference
    )
    {
        ThrowIfMeshNode(typeof(TReduced));
        return (ISynchronizationStream<TReduced>)
            GetSynchronizationStreamMethod
                .MakeGenericMethod(typeof(TReduced), reference.GetType())
                .Invoke(this, [id, reference])!;
    }


    // Points at the UNCHECKED implementation: the public dynamic-dispatch overload above
    // has already run WarnIfMeshNode, so the reflective hop must NOT re-enter the guarded
    // public path (which would double-warn). Sanctioned internal callers reach the same
    // unchecked body directly.
    private static readonly MethodInfo GetSynchronizationStreamMethod =
        ReflectionHelper.GetMethodGeneric<Workspace>(x =>
            x.GetRemoteStreamUnchecked<object, WorkspaceReference<object>>(null!, null!)
        );


    /// <inheritdoc />
    public ISynchronizationStream<TReduced> GetRemoteStream<TReduced, TReference>(
        Address owner,
        TReference reference
    )
        where TReference : WorkspaceReference
    {
        ThrowIfMeshNode(typeof(TReduced));
        return GetRemoteStreamUnchecked<TReduced, TReference>(owner, reference);
    }

    // 🚨 The single sanctioned escape hatch behind the GetRemoteStream<MeshNode> guard.
    // internal (+ InternalsVisibleTo) so the shared mesh-node cache and the MeshNode
    // reduce-callback plumbing can open the raw remote reduce; never throws for MeshNode.
    internal ISynchronizationStream<TReduced> GetRemoteStreamUnchecked<TReduced, TReference>(
        Address owner,
        TReference reference
    )
        where TReference : WorkspaceReference =>
        Hub.Address.Equals(owner)
            ? throw new ArgumentException("Owner cannot be the same as the subscriber.")
            : GetExternalClientSynchronizationStream<TReduced, TReference>(owner, reference);

    /// <summary>
    /// Gets a remote stream with hub impersonation. The subscribing hub's address
    /// becomes the identity on the SubscribeRequest, ensuring hub-to-hub subscriptions
    /// use the hub's identity instead of any ambient user context.
    /// </summary>
    public ISynchronizationStream<EntityStore> GetRemoteStreamAsHub(
        Address owner,
        WorkspaceReference<EntityStore> reference
    ) =>
        Hub.Address.Equals(owner)
            ? throw new ArgumentException("Owner cannot be the same as the subscriber.")
            : (ISynchronizationStream<EntityStore>)this.CreateExternalClient<EntityStore, WorkspaceReference<EntityStore>>(owner, reference, impersonateAsHub: true);


    // 🚨 Lazy<T> wraps the factory because check-then-act ConcurrentDictionary
    // races would otherwise spawn duplicate upstream subscriptions: two
    // concurrent callers each pass TryGetValue (miss), each call
    // CreateExternalClient (which opens a SubscribeRequest to the owning hub
    // — a real side effect), and the second `_remoteStreamCache[key] = …`
    // overwrites the first. The orphaned stream remains subscribed and
    // continues consuming, doubling the emissions seen on the wire.
    // Lazy<T>(LazyThreadSafetyMode.ExecutionAndPublication) guarantees the
    // factory body runs at most once per key, regardless of contention.
    // Symptom this fixes: streaming-text test sequence
    // `[0, 19, 22, 19, 22, 46]` — every patch delivered twice via the
    // orphaned stream.
    //
    // 🚨 IDENTITY IS PART OF THE KEY, and must stay that way.
    // A remote stream is NOT identity-neutral: CreateExternalClient stamps the subscribing
    // user onto the SubscribeRequest and the OWNER evaluates its RLS gate ONCE, at subscribe
    // time, for exactly that user. The stream therefore carries that user's permission view
    // for its entire life. Keyed on (owner, reference) alone — as it was until this comment —
    // the FIRST reader on a workspace fixed the permission view every later reader inherited,
    // in both directions: a permitted reader's stream disclosed content to a denied reader,
    // and a denied reader's refused stream made a permitted reader's view render empty. That
    // is a cross-user information disclosure wherever one workspace serves two identities
    // (the shared `portal/anonymous` hub, MCP/REST session hubs with an `anon` fallback).
    // Regression: MeshWeaver.Security.Test/RemoteStreamCacheIdentityTest.
    //
    // The identity component MUST come from JsonSynchronizationStream.ResolveSubscribeIdentity
    // — the same function that stamps SubscribeRequest.Identity — so key and subscribe can
    // never drift apart.
    private readonly ConcurrentDictionary<(Address Owner, WorkspaceReference Reference, string Identity), Lazy<ISynchronizationStream>> _remoteStreamCache = new();

    // Streams that EvictForPath removed from the cache but did NOT dispose (their
    // live subscribers keep them attached). The workspace still OWNS their lifetime —
    // each carries a per-stream `sync/` hub whose 5s stale-callback scanner roots it
    // in the global TimerQueue, so an evicted-and-never-disposed stream leaks its hub
    // forever (the RunLevel=1 MeshHub_IsCollected failure). Disposed in Dispose
    // alongside the still-cached streams, re-establishing the workspace-rooted
    // disposal that eviction severed — OR earlier, per identity, by
    // <see cref="DetachRemoteStreams"/> when the shared mesh-node cache idle-releases a
    // path (the idle release proves no consumer remains attached, so waiting for
    // workspace disposal would leak the stream's heartbeat for the process lifetime).
    // Keyed set (value unused) rather than a bag so a per-identity release can remove
    // matching entries without the drain-and-re-add race a ConcurrentBag would force.
    // 🚨 MUST hash by REFERENCE: SynchronizationStream is a record whose generated
    // GetHashCode recurses into StreamConfiguration (which holds the stream back) —
    // value-hashing a stream instance stack-overflows the process. Stream identity
    // here IS the instance, so reference semantics is also the correct semantics.
    private readonly ConcurrentDictionary<ISynchronizationStream, byte> _evictedRemoteStreams =
        new(StreamReferenceComparer.Instance);

    /// <summary>Reference-identity comparer for stream instances — see the
    /// <see cref="_evictedRemoteStreams"/> field note (record GetHashCode recursion).</summary>
    private sealed class StreamReferenceComparer : IEqualityComparer<ISynchronizationStream>
    {
        public static readonly StreamReferenceComparer Instance = new();
        public bool Equals(ISynchronizationStream? x, ISynchronizationStream? y) => ReferenceEquals(x, y);
        public int GetHashCode(ISynchronizationStream obj) =>
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }

    /// <summary>
    /// Atomically removes every remote synchronization stream for
    /// (<paramref name="owner"/>, <paramref name="reference"/>) from BOTH the live cache
    /// (<see cref="_remoteStreamCache"/>) and the change-feed-evicted parking set
    /// (<see cref="_evictedRemoteStreams"/>), WITHOUT disposing them, and returns them.
    /// From the moment this returns, a concurrent <c>GetRemoteStream</c> for the same
    /// identity builds a FRESH stream — it can no longer adopt a detached instance, so
    /// the caller may safely dispose the returned streams once it has proven no
    /// consumer is attached (the shared mesh-node cache's idle release does exactly
    /// that: detach → re-verify zero subscribers under its entry gate → dispose; on a
    /// lost race it puts them back via <see cref="ParkRemoteStreams"/>).
    /// Disposing a detached stream posts <c>UnsubscribeRequest</c> to the owner (the
    /// owner-side mirror unsubscribes) and disposes the per-stream <c>sync/</c> hub —
    /// its 45s heartbeat dies with it. This method itself only DETACHES, never closes
    /// and never re-subscribes.
    /// </summary>
    internal IReadOnlyList<ISynchronizationStream> DetachRemoteStreams(
        Address owner, WorkspaceReference reference)
    {
        var detached = new List<ISynchronizationStream>();
        // 🚨 Scan, don't index: the cache is keyed (owner, reference, IDENTITY), so one
        // (owner, reference) pair can hold one stream PER identity. Detaching only a single
        // identity's entry would leave the others attached and defeat the caller's
        // "no consumer remains" proof (the shared mesh-node cache's idle release).
        foreach (var key in _remoteStreamCache.Keys)
        {
            if (!key.Owner.Equals(owner) || !Equals(key.Reference, reference))
                continue;
            if (!_remoteStreamCache.TryRemove(key, out var cached))
                continue;
            // A cached Lazy is materialised immediately by its creator
            // (GetExternalClientSynchronizationStream calls .Value right after
            // GetOrAdd); taking .Value here at worst briefly waits for that
            // factory so the instance is never orphaned half-created.
            try
            {
                detached.Add(cached.Value);
            }
            catch (Exception ex)
            {
                // Factory faulted — there is no stream to own; the creator saw
                // the same exception on its own .Value access.
                _logger.LogDebug(ex,
                    "Workspace {WorkspaceId} skipped detaching faulted remote stream for {Owner}",
                    Id, owner);
            }
        }
        foreach (var parked in _evictedRemoteStreams.Keys)
        {
            if (parked.Owner.Equals(owner)
                && Equals(parked.Reference, reference)
                && _evictedRemoteStreams.TryRemove(parked, out _))
                detached.Add(parked);
        }
        return detached;
    }

    /// <summary>
    /// Returns streams obtained from <see cref="DetachRemoteStreams"/> to the
    /// evicted-stream parking set when the caller lost its release race (a consumer
    /// re-attached between detach and the final zero-subscriber check). The workspace
    /// re-owns their lifetime: they stay live for their attached subscribers and are
    /// disposed by a later successful release or by <see cref="Dispose"/>.
    /// </summary>
    internal void ParkRemoteStreams(IReadOnlyList<ISynchronizationStream> streams)
    {
        foreach (var stream in streams)
            _evictedRemoteStreams[stream] = 0;
    }

    private ISynchronizationStream<TReduced> GetExternalClientSynchronizationStream<
        TReduced,
        TReference
    >(Address address, TReference reference)
        where TReference : WorkspaceReference
    {
        // 🚨 The subscribing identity is part of the key — see the _remoteStreamCache field note.
        // Resolved from the SAME helper that stamps SubscribeRequest.Identity so a stream can
        // only ever be served back to the identity it was subscribed for.
        var key = (address, (WorkspaceReference)reference,
            JsonSynchronizationStream.ResolveSubscribeIdentity(_accessService).Id);

        while (true)
        {
            // GetOrAdd with a Lazy<T> factory: the factory may run multiple
            // times to produce candidate Lazy objects, but only ONE wins the
            // dictionary slot and ALL callers see THAT one. The inner Lazy
            // (ExecutionAndPublication) then runs its expensive
            // CreateExternalClient body exactly once. Net: one stream per
            // key, never two competing live subscriptions.
            var lazy = _remoteStreamCache.GetOrAdd(key,
                _ => new Lazy<ISynchronizationStream>(
                    () => (ISynchronizationStream)this.CreateExternalClient<TReduced, TReference>(address, reference),
                    LazyThreadSafetyMode.ExecutionAndPublication));

            var stream = lazy.Value;

            // Check if cached stream is still alive. Hub.RunLevel alone is not
            // sufficient because hub shutdown is async: right after stream.Dispose()
            // is called, RunLevel is still Running even though disposal was triggered.
            // Cast to the concrete type to access IsDisposing (not on interface).
            if (stream.Hub?.RunLevel <= MessageHubRunLevel.Started
                && stream.Hub is not MessageHub { IsDisposing: true })
                return (ISynchronizationStream<TReduced>)stream;

            // Dead — remove (if still ours) and retry. The TryRemove guards
            // against the case where another thread already replaced the
            // entry: only the original Lazy is removed.
            ((ICollection<KeyValuePair<(Address Owner, WorkspaceReference Reference, string Identity), Lazy<ISynchronizationStream>>>)_remoteStreamCache)
                .Remove(new KeyValuePair<(Address Owner, WorkspaceReference Reference, string Identity), Lazy<ISynchronizationStream>>(key, lazy));
        }
    }







    /// <inheritdoc />
    public IObservable<ActivityLog> Update(IReadOnlyCollection<object> instances, UpdateOptions updateOptions) =>
        RequestChange(
            new DataChangeRequest()
            {
                Updates = instances.ToImmutableList(),
                Options = updateOptions,
                ChangedBy = null
            }
        );



    /// <inheritdoc />
    public IObservable<ActivityLog> Delete(IReadOnlyCollection<object> instances) =>
        RequestChange(
            new DataChangeRequest { Deletions = instances.ToImmutableList(), ChangedBy = null }
        );

    // 🚨 LOCAL reduced streams are cached exactly like the REMOTE ones above — because a reduce
    // is NOT free and NOT garbage-collectable.
    //
    // Every ReduceManager.ReduceStream constructs a SynchronizationStream, and a
    // SynchronizationStream constructs a hosted `sync/{id}` sub-hub (its own Autofac lifetime
    // scope, TypeRegistry and JsonSerializerOptions — ~140 KB apiece), then registers itself for
    // disposal ON ITS PARENT (WorkspaceStreams.CreateReducedStream). The parent is the data
    // source's primary stream, which lives as long as the hub. So an UNCACHED GetStream leaks a
    // hub per CALL, released only when the whole hub dies.
    //
    // Nothing about a plain reduce is caller-specific, and the callers are hot: the
    // PatchDataRequest handler resolves the target stream this way on EVERY cross-hub write, and
    // MeshNodeStreamHandle does it on every own-node read and every own-node Update. One NodeType
    // recompile made ~60 of these; a portal morning of GitSync re-imports made enough to drive
    // memex-cloud from 2.5 GB to 24.5 GB at 130–340 MB/min (Systemorph/MeshWeaver#1324).
    //
    // A caller-supplied `configuration` DOES make the stream caller-specific (client id,
    // subscriber, initialization callback, property bag), so those stay uncached — the same
    // split the remote cache draws by keying on the subscribing identity.
    private readonly ConcurrentDictionary<WorkspaceReference, Lazy<ISynchronizationStream>> _localStreamCache = new();

    /// <inheritdoc />
    public ISynchronizationStream<TReduced> GetStream<TReduced>(
        WorkspaceReference<TReduced> reference,
        Func<StreamConfiguration<TReduced>, StreamConfiguration<TReduced>>? configuration
        )
    {
        if (configuration is not null)
            return ReduceLocalStream(reference, configuration);

        while (true)
        {
            var lazy = _localStreamCache.GetOrAdd(reference,
                _ => new Lazy<ISynchronizationStream>(
                    () => ReduceLocalStream(reference, null),
                    LazyThreadSafetyMode.ExecutionAndPublication));

            ISynchronizationStream stream;
            try
            {
                stream = lazy.Value;
            }
            catch
            {
                // A Lazy in ExecutionAndPublication mode CACHES the exception, so a transient
                // failure (e.g. HubDisposingException from a hub that is winding down) would
                // otherwise poison this reference for the workspace's life. Drop the faulted
                // entry — only if it is still ours — and let the caller see the original fault.
                ((ICollection<KeyValuePair<WorkspaceReference, Lazy<ISynchronizationStream>>>)_localStreamCache)
                    .Remove(new KeyValuePair<WorkspaceReference, Lazy<ISynchronizationStream>>(reference, lazy));
                throw;
            }

            // Same liveness contract as GetExternalClientSynchronizationStream: a cached stream
            // whose sub-hub is gone (its parent was torn down, or a consumer disposed it) is
            // replaced rather than handed out dead.
            if (stream.Hub?.RunLevel <= MessageHubRunLevel.Started
                && stream.Hub is not MessageHub { IsDisposing: true })
                return (ISynchronizationStream<TReduced>)stream;

            ((ICollection<KeyValuePair<WorkspaceReference, Lazy<ISynchronizationStream>>>)_localStreamCache)
                .Remove(new KeyValuePair<WorkspaceReference, Lazy<ISynchronizationStream>>(reference, lazy));
        }
    }

    private ISynchronizationStream<TReduced> ReduceLocalStream<TReduced>(
        WorkspaceReference<TReduced> reference,
        Func<StreamConfiguration<TReduced>, StreamConfiguration<TReduced>>? configuration)
        => (ISynchronizationStream<TReduced>?)ReduceManager.ReduceStream(
               this,
               reference,
               configuration
           ) ?? throw new InvalidOperationException("Failed to create stream");

    /// <inheritdoc />
    public ISynchronizationStream<EntityStore> GetStream(params Type[] types)
        => (ISynchronizationStream<EntityStore>?)
            ReduceManager.ReduceStream<EntityStore>(
    this,
    new CollectionsReference(types
        .Select(t =>
            DataContext.TypeRegistry.TryGetCollectionName(t, out var name)
                ? name
                : throw new ArgumentException($"Type {t.FullName} is unknown.")
        ).ToArray()!),
    x => x) ?? throw new InvalidOperationException("Failed to create stream");

    /// <inheritdoc />
    public ReduceManager<EntityStore> ReduceManager => DataContext.ReduceManager;

    /// <inheritdoc />
    public IMessageHub Hub { get; }
    /// <summary>The workspace identity, equal to the owning hub's address.</summary>
    public object Id => Hub.Address;


    /// <inheritdoc />
    public DataContext DataContext { get; }

    /// <inheritdoc />
    public IObservable<ActivityLog> RequestChange(DataChangeRequest change)
        => this.Change(change);

    private bool isDisposing;

    /// <summary>
    /// Disposes the workspace: drains registered disposables, disposes cached and evicted remote
    /// streams (tearing down their per-stream sync hubs), disposes the data context, and
    /// unsubscribes from the change feed. Idempotent.
    ///
    /// <para>🚨 SYNCHRONOUS and reactive-only — no <c>async</c>, no <c>await</c>, no
    /// <see cref="ValueTask"/>. Teardown is a synchronous Dispose that cancels and joins; every
    /// step below is either an <c>IDisposable.Dispose()</c> (which unsubscribes an Rx subscription)
    /// or a synchronous unhook.</para>
    ///
    /// <para>This used to be <c>async ValueTask DisposeAsync()</c> whose only <c>await</c> drained a
    /// bag of <see cref="IAsyncDisposable"/>. That bag was ALWAYS EMPTY: the
    /// <c>AddDisposable(IAsyncDisposable)</c> overload had zero callers across src/ and test/ — the
    /// single caller (ThreadSubmission) passes an Rx subscription, which binds the IDisposable
    /// overload. So the await was dead code that nonetheless made the whole teardown path async,
    /// and its continuation captured whatever scheduler disposal ran on. Workspace disposal IS
    /// reachable from a hub action block, so that continuation could be queued behind the very turn
    /// waiting for disposal to finish.</para>
    /// </summary>
    public void Dispose()
    {
        _logger.LogInformation("Workspace {WorkspaceId} starting Dispose, Thread: {ThreadId}",
            Id, Thread.CurrentThread.ManagedThreadId);

        if (isDisposing)
        {
            _logger.LogDebug("Workspace {WorkspaceId} already disposing, returning", Id);
            return;
        }
        isDisposing = true;

        _logger.LogDebug("Workspace {WorkspaceId} disposing {SyncCount} sync disposables", Id, disposables.Count);
        while (disposables.TryTake(out var d))
        {
            try
            {
                d.Dispose();
                _logger.LogTrace("Workspace {WorkspaceId} disposed sync disposable {DisposableType}", Id, d.GetType().Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Workspace {WorkspaceId} error disposing sync disposable {DisposableType}", Id, d.GetType().Name);
            }
        }

        // Dispose any cached remote streams that haven't been removed yet. Each
        // SynchronizationStream registers its own SubscribeRequest hub.Observe
        // callback for disposal here; without this loop the parent hub's
        // responseSubjects entry for each open SubscribeRequest leaks past the
        // test base's quiescing-budget leak check.
        if (!_remoteStreamCache.IsEmpty)
        {
            _logger.LogDebug("Workspace {WorkspaceId} disposing {RemoteStreamCount} cached remote streams",
                Id, _remoteStreamCache.Count);
            foreach (var key in _remoteStreamCache.Keys)
            {
                if (_remoteStreamCache.TryRemove(key, out var cached))
                {
                    // Skip if the Lazy was never materialised — no stream
                    // was actually created, nothing to dispose.
                    if (!cached.IsValueCreated) continue;
                    try { cached.Value.Dispose(); }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Workspace {WorkspaceId} error disposing remote stream {Key}",
                            Id, key);
                    }
                }
            }
        }

        // Streams evicted by the change feed (removed from _remoteStreamCache without
        // disposal) are still workspace-owned — dispose them here so their `sync/`
        // hubs (and the TimerQueue-rooting stale-callback scanner) are torn down.
        // Idempotent with subscriber-driven disposal: SynchronizationStream.Dispose
        // is safe to call twice.
        foreach (var evicted in _evictedRemoteStreams.Keys)
        {
            if (!_evictedRemoteStreams.TryRemove(evicted, out _))
                continue;
            try { evicted.Dispose(); }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Workspace {WorkspaceId} error disposing evicted remote stream", Id);
            }
        }

        // Local reduced streams are OWNED by their parent (CreateReducedStream registers each on
        // the data-source stream that produced it), so DataContext.Dispose below tears them down.
        // Drop the index so the workspace stops referencing them from here on.
        _localStreamCache.Clear();

        _logger.LogDebug("Workspace {WorkspaceId} disposing DataContext", Id);
        try
        {
            DataContext.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Workspace {WorkspaceId} error disposing DataContext", Id);
        }

        try { _changeFeedSubscription?.Dispose(); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Workspace {WorkspaceId} failed to dispose change-feed subscription", Id);
        }

        _logger.LogInformation("Workspace {WorkspaceId} Dispose completed", Id);
    }
    private readonly ConcurrentBag<IDisposable> disposables = new();

    /// <inheritdoc />
    public void AddDisposable(IDisposable disposable)
    {
        if (isDisposing)
        {
            // Same contract as MessageHub.RegisterForDisposal: a registrant added after
            // disposal has begun is disposed IMMEDIATELY. The drain loop in Dispose
            // has already run, so bagging it would leak it — and any Rx Timeout timer it
            // roots via the global TimerQueue — past the container's lifetime (the
            // post-teardown ObjectDisposedException straggler class).
            try { disposable.Dispose(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Workspace {WorkspaceId} error disposing late-registered disposable {DisposableType}",
                    Id, disposable.GetType().Name);
            }
            return;
        }
        disposables.Add(disposable);
    }


    /// <inheritdoc />
    public ISynchronizationStream<EntityStore>? GetStream(StreamIdentity identity)
    {
        var ds = DataContext.GetDataSourceForId(identity.Owner);
        return ds?.GetStreamForPartition(identity.Partition);
    }


    /// <summary>
    /// Handles a <see cref="DataChangeResponse"/>: marks the delivery processed when the change
    /// committed, otherwise ignores it.
    /// </summary>
    /// <param name="response">The data change response delivery to handle.</param>
    /// <returns>The processed or ignored delivery.</returns>
    protected IMessageDelivery HandleCommitResponse(IMessageDelivery<DataChangeResponse> response)
    {
        if (response.Message.Status == DataChangeStatus.Committed)
            return response.Processed();
        // TODO V10: Here we have to put logic to revert the state if commit has failed. (26.02.2024, Roland Bürgi)
        return response.Ignored();
    }

    void IWorkspace.SubscribeToClient(
        IMessageDelivery<SubscribeRequest> delivery
    )
    {
        var referenceType = delivery.Message.Reference.GetType();
        var genericWorkspaceType = referenceType;
        while (!genericWorkspaceType!.IsGenericType || genericWorkspaceType.GetGenericTypeDefinition() != typeof(WorkspaceReference<>))
        {
            genericWorkspaceType = genericWorkspaceType.BaseType;
        }

        var reducedType = genericWorkspaceType.GetGenericArguments().First();
        SubscribeToClientMethod
            .MakeGenericMethod(reducedType, referenceType)
            .Invoke(this, [delivery]);
    }


    private static readonly MethodInfo SubscribeToClientMethod =
        ReflectionHelper.GetMethodGeneric<Workspace>(x =>
            x.SubscribeToClient<object, WorkspaceReference<object>>(null!)
        );

    private void SubscribeToClient<TReduced, TReference>(IMessageDelivery<SubscribeRequest> delivery)
        where TReference : WorkspaceReference<TReduced>
    {
        this.CreateSynchronizationStream<TReduced, TReference>(delivery);
    }

    // 🚨 ONE server-side stream per (subscriber, StreamId) — issue #606.
    //
    // A SubscribeRequest carries the SUBSCRIBER's StreamId, and a resubscribe deliberately
    // REUSES it ("refresh MY stream" — JsonSynchronizationStream.Resubscribe). Before this
    // registry the owner treated every arrival as a brand-new subscription and built another
    // reduced stream — for a LayoutAreaReference that is a whole new LayoutAreaHost + render
    // graph (LayoutExtensions' AddWorkspaceReferenceStream factory) — while the previous one
    // kept running, kept rendering and kept pushing DataChangedEvents to the same subscriber.
    // Nothing ever released it: only an UnsubscribeRequest disposes a server-side stream, and
    // a resubscribe never sends one.
    //
    // That made the accumulation unbounded on the ordinary serving path: the layout-area
    // stream's staleness gate is INERT for EntityStore reductions (EntityStore has no
    // `long Version`, so `receivedVersion` never advances while `announcedVersion` is
    // fabricated as received+1 — see the version gate in JsonSynchronizationStream), so
    // EVERY change-feed event on the owner path resubscribes. Measured: one write to a node
    // added one more live render pipeline, so a single subsequent write produced 1 → 10 Full
    // frames after 8 writes, each leaked host holding its EntityStore, its menu/node-stream
    // subscriptions (which pin MeshNodeStreamCache entries so their upstream sync streams can
    // never be idle-released) and its whole control tree.
    //
    // The registry is an INSTANCE field on the workspace, so its lifetime IS the owner hub's
    // (no static state, and an owner recycle correctly starts from empty → a genuinely
    // orphaned mirror still gets a fresh stream).
    private readonly ConcurrentDictionary<(string Subscriber, string StreamId), ClientSubscription>
        _clientSubscriptions = new();

    // 🚨 A CLASS, never a record: SynchronizationStream is itself a record whose generated
    // GetHashCode/Equals recurse into StreamConfiguration (which holds the stream back) — a
    // record wrapper around one stack-overflows the process the moment the dictionary hashes
    // or compares it. See the _evictedRemoteStreams field note.
    private sealed class ClientSubscription(WorkspaceReference reference, ISynchronizationStream stream)
    {
        public WorkspaceReference Reference { get; } = reference;
        public ISynchronizationStream Stream { get; } = stream;
    }

    /// <summary>
    /// Returns the server-side stream already serving <paramref name="streamId"/> for
    /// <paramref name="subscriber"/> under the SAME reference, or <c>null</c>. A hit means the
    /// arriving SubscribeRequest is a RE-subscribe of a stream this owner still serves, so the
    /// caller must re-assert the current snapshot on it instead of building a second one.
    /// </summary>
    internal ISynchronizationStream? GetClientSubscription(
        Address? subscriber, string? streamId, WorkspaceReference reference)
    {
        if (subscriber is null || string.IsNullOrEmpty(streamId))
            return null;
        if (!_clientSubscriptions.TryGetValue((subscriber.ToString(), streamId), out var existing))
            return null;
        if (!Equals(existing.Reference, reference))
            return null;
        // A stream whose sync hub is past Started is tearing down — its registry entry is
        // removed at ShutDown, so this window is short; treat it as absent and let the caller
        // build a fresh stream rather than re-asserting into a corpse.
        return existing.Stream.Hub is { RunLevel: <= MessageHubRunLevel.Started }
            ? existing.Stream
            : null;
    }

    /// <summary>
    /// Records <paramref name="stream"/> as THE server-side stream for
    /// (<paramref name="subscriber"/>, <paramref name="streamId"/>) and unregisters it when the
    /// stream is disposed, so the entry can never outlive what it points at.
    /// </summary>
    internal void RegisterClientSubscription(
        Address? subscriber, string? streamId, WorkspaceReference reference, ISynchronizationStream stream)
    {
        if (subscriber is null || string.IsNullOrEmpty(streamId))
            return;
        var key = (subscriber.ToString(), streamId);
        _clientSubscriptions[key] = new ClientSubscription(reference, stream);
        // Pair-exact removal by REFERENCE identity (never value equality — see ClientSubscription):
        // a later stream that legitimately took over this key must not be unregistered by an
        // earlier stream's teardown.
        stream.RegisterForDisposal(System.Reactive.Disposables.Disposable.Create(() =>
        {
            if (_clientSubscriptions.TryGetValue(key, out var current)
                && ReferenceEquals(current.Stream, stream))
                _clientSubscriptions.TryRemove(key, out _);
        }));
    }


}
