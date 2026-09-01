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
// The graph/compiler split (#2967): the shared contract carries the synced-query helpers that
// used to live in MeshWeaver.Graph, and the compile pipeline moved to MeshWeaver.Compiler.
[assembly: InternalsVisibleTo("MeshWeaver.Graph.Contract")]
[assembly: InternalsVisibleTo("MeshWeaver.Compiler")]
[assembly: InternalsVisibleTo("MeshWeaver.Compiler.Pipeline")]
// Framework-internal tests of the raw remote-stream cache identity
// (ReferenceEquals on Workspace._remoteStreamCache) legitimately open the raw
// single-node reduce — they test the mechanism itself, not mesh-node access.
[assembly: InternalsVisibleTo("MeshWeaver.Query.Test")]
// Owner-side merge internals (ApplyMeshNodeMerge / RebaseMonotonicTriggers): the
// monotonic-trigger merge semantics are pinned by deterministic unit tests
// (MonotonicTriggerMergeTest) against the exact internal seam the
// PatchDataRequest handler runs — not a re-implementation of the call sequence.
[assembly: InternalsVisibleTo("MeshWeaver.Data.Test")]
// The dead-subscriber eviction end-to-end test (issues #2426/#2546) asserts on the
// client-subscription registry of a per-node hub INSIDE the in-process silo — the only
// observation point that proves the router's TargetUnserved NACK actually reached the owner
// and disposed the leaked server-side stream, rather than merely that some log line appeared.
[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Orleans.Test")]

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

        // 🚨 Give the hub the goodbye it cannot say for itself — see RecycleAnnouncement and
        // AnnounceRecycleToClientSubscriptions. Registered here because the client-subscription
        // registry lives on THIS workspace, and it is the only thing that knows who is listening.
        hub.Set(new RecycleAnnouncement(AnnounceRecycleToClientSubscriptions));
    }

    /// <summary>
    /// 🚨 <b>Tells every live client subscription that its owner is being RECYCLED</b> — the
    /// missing half of <c>StreamEndedEvent</c>, whose own contract already promises the case
    /// ("the owning hub tearing down — deactivation, recycle, restart") that its emitter
    /// deliberately refuses to send, because a dying hub must never speak (see
    /// <c>JsonSynchronizationStream</c>'s teardown guard, and <see cref="RecycleAnnouncement"/>
    /// for the full history).
    ///
    /// <para><b>Called on the recycled hub's own turn</b>, before its disposal starts — so the
    /// registry below is intact and the parent hub is resolvable. What it does NOT do is post
    /// anything yet: the delivery is deferred to <see cref="IMessageHub.DisposalCompleted"/> and
    /// carried by the PARENT hub, which outlives this one. Both halves of that are load-bearing:</para>
    /// <list type="bullet">
    /// <item><description><b>Posted by the parent</b>, because a hub that is tearing down cannot
    /// reliably deliver its own last message — and the version of this that reached up the tree
    /// from INSIDE the teardown resurrected the very Orleans activation it was retiring
    /// (OrleansGrainTeardownStragglerTest). Here the parent speaks about a recycle it hosted, to a
    /// third party, with nothing routed at the dead address.</description></item>
    /// <item><description><b>After DisposalCompleted</b>, because the subscriber answers this
    /// event with ONE bounded re-ask (JsonSynchronizationStream's recycle re-arm). Announcing
    /// earlier makes that re-ask race the teardown: it lands on the still-dying instance, is
    /// NACKed <c>ShuttingDown</c>, and burns a budget that exists for a genuinely non-converging
    /// owner — the #1360 shape, "four rejections in 11 ms". Waiting for the signal the hub already
    /// publishes costs no timer, no poll and no watchdog.</description></item>
    /// </list>
    ///
    /// <para>Fires at most once per stream (the registry is emptied by the disposal that follows),
    /// and never at all when nothing is subscribed — a recycle of an unwatched hub stays exactly
    /// as cheap as it was.</para>
    /// </summary>
    private void AnnounceRecycleToClientSubscriptions()
    {
        // Snapshot NOW: the streams (and with them these registry entries) are torn down by the
        // disposal we are about to precede.
        var orphaned = _clientSubscriptions
            .Select(kv => (kv.Value.Subscriber, kv.Key.StreamId))
            .ToArray();
        if (orphaned.Length == 0)
            return;

        // The carrier must OUTLIVE the target. A per-node hub is hosted by the mesh hub, a client
        // hub by whoever created it; when there is no parent there is nothing that can still speak
        // after we are gone, and the subscriber falls back to the pre-fix recovery (the owner's
        // next write, or a reload).
        IMessageHub? carrier;
        try
        {
            carrier = Hub.Configuration.ParentHub;
        }
        catch (Exception ex)
        {
            // ParentHub re-resolves from the parent service provider, which can already be
            // disposed when a whole tree is going down. Probing must never throw into a teardown.
            _logger.LogDebug(ex,
                "Workspace {WorkspaceId}: could not resolve a carrier for the recycle announcement",
                Id);
            return;
        }

        // 🚨 "No one must ever publish from main hub" (maintainer, 2026-09-01). For a per-node or
        // activity hub the resolved parent IS the mesh ROUTER, and a router-stamped announcement
        // is exactly what ROUTER_TRAFFIC reports as a violation — the compile+render gate logged
        // "StreamEndedEvent has the mesh hub as sender (sender: mesh/…)" once per recycled
        // activity. Climb to the router's own parent — a real application hub that outlives
        // per-node hubs — and announce only from a NON-router carrier; when none exists the
        // change-feed-latch fallback below already covers every subscriber.
        if (carrier is not null
            && string.Equals(carrier.Address.Type, AddressExtensions.MeshType, StringComparison.Ordinal))
        {
            // The router's designated spokesman (RouterCarrier — on a mesh, the nodeops execution
            // hub); with none registered, climb to the router's own parent. Either way the
            // announcement never carries the router's identity.
            IMessageHub? spokesman = null;
            try { spokesman = carrier.Configuration.Get<RouterCarrier>()?.Resolve(carrier); }
            catch { /* teardown probing must never throw */ }
            if (spokesman is null)
            {
                IMessageHub? up;
                try { up = carrier.Configuration.ParentHub; }
                catch { up = null; }
                spokesman = up is null || ReferenceEquals(up, carrier)
                            || string.Equals(up.Address.Type, AddressExtensions.MeshType, StringComparison.Ordinal)
                    ? null : up;
            }
            carrier = spokesman;
        }

        // 🚨 A SELF-PARENT IS NOT A CARRIER. `Configuration.ParentHub` resolves `IMessageHub` from
        // the parent DI scope, and for a root hub that is the hub ITSELF (the same self-parent
        // DataExtensions.RouteStreamMessage terminates its walk on). Posting through it would be
        // posting from the dying hub — exactly the thing this indirection exists to avoid.
        if (carrier is null || ReferenceEquals(carrier, Hub) || carrier.Address.Equals(Hub.Address))
        {
            _logger.LogDebug(
                "Workspace {WorkspaceId}: recycled hub has no parent to announce through — "
                + "{Count} live subscription(s) fall back to the change-feed latch",
                Id, orphaned.Length);
            return;
        }

        _logger.LogDebug(
            "Workspace {WorkspaceId}: recycling — announcing the end of {Count} live client "
            + "subscription(s) through {Carrier} once teardown completes",
            Id, orphaned.Length, carrier.Address);

        void Announce()
        {
            foreach (var (subscriber, streamId) in orphaned)
            {
                try
                {
                    carrier.Post(new StreamEndedEvent(streamId), o => o.WithTarget(subscriber));
                }
                catch (Exception ex)
                {
                    // Best-effort per subscriber: one unreachable mirror must not silence the rest.
                    // StreamEndedEvent is [CanBeIgnored], so a subscriber that has itself gone away
                    // is dropped by routing rather than NACKed.
                    _logger.LogDebug(ex,
                        "Workspace {WorkspaceId}: could not announce the recycle of stream "
                        + "{StreamId} to {Subscriber}", Id, streamId, subscriber);
                }
            }
        }

        // Event-driven, not timed: DisposalCompleted is a ReplaySubject(1) that the hub signals as
        // the last act of its teardown (the disposal watchdog force-completes it if the phased path
        // ever wedges), so this leg is already guaranteed terminal and needs no deadline of its own.
        Hub.DisposalCompleted
            .Take(1)
            .Subscribe(
                _ => Announce(),
                // A FAULTED disposal is still a disposal: the address is gone either way, and
                // leaving the subscriber silent is the defect this exists to fix. The fault itself
                // is surfaced by the disposal path, never swallowed here.
                ex =>
                {
                    _logger.LogDebug(ex,
                        "Workspace {WorkspaceId}: disposal faulted — announcing the recycle anyway",
                        Id);
                    Announce();
                });
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
    ///
    /// <para>🚨🚨 <b>DO NOT make this conditional on the stream still being LIVE.</b> It is the
    /// obvious change to make — the mirror looks healthy, the owner's own fan-out delivers routine
    /// updates, and the two other consumers of this same <c>IMeshChangeFeed</c> broadcast already
    /// say so in as many words (<c>MeshNodeStreamCache.ResetFailureState</c>: <i>"A healthy live
    /// entry is left untouched"</i>; <c>JsonSynchronizationStream</c>'s version-gated
    /// <c>Resubscribe</c>: <i>"a HEALTHY subscriber receives that same write through its own
    /// subscription, so resubscribing on it is pure churn"</i>). It was tried, measured, and
    /// REVERTED — see <c>Doc/Architecture/LiveMirrorsAndTheChangeFeed</c> for the numbers.</para>
    ///
    /// <para><b>Why it cannot simply go.</b> This eviction is, incidentally, what keeps a
    /// cross-hub writer's BASE current with respect to writes the OWNER makes for itself. The
    /// per-path update queue hands a predecessor's locally-computed node to its successor
    /// (<c>_pendingSelfWrites</c>), but that only carries THIS cache's writes forward; an
    /// owner-side write (an activity's <c>messageCount</c>, a sealed log segment) reaches the
    /// mirror only through the asynchronous fan-out. Evicting on the change event forces the next
    /// write to resolve a fresh stream and therefore to diff against a freshly-fetched
    /// authoritative snapshot. Skip it and the base goes stale: measured with
    /// <c>DOTNET_PROCESSOR_COUNT=4</c> on
    /// <c>StaticRepoImportActivityWriteCountTest.AppendCost_DoesNotGrowWithTheLengthOfTheActivity</c>,
    /// the gated version LOST a whole 25-message append batch (2000 appended, 1975 recorded) while
    /// the ungated one passed, and on CI it turned 80 appends into 99 writes with 44
    /// <c>OWNER_NACK_REENQUEUE</c> / <c>MergeGuard</c> refusals of <c>messageCount</c>.</para>
    ///
    /// <para><b>What it costs, and what that cost looks like in a log.</b> The feed fires after
    /// EVERY create/update/delete, so every cross-hub write evicts its own mirror; a write holds a
    /// lease only for its <c>Observable.Create</c> subscription, so moments later
    /// <see cref="ReclaimIfUnheld"/> DISPOSES the evicted stream — <c>UnsubscribeRequest</c> to the
    /// owner, both <c>sync/{id}</c> hubs gone — and the owner then announces
    /// <c>StreamEndedEvent</c> to a subscriber that no longer exists. Measured: six progress writes
    /// to one activity node with one live reader mint 7 client-side and 11 owner-side <c>sync/</c>
    /// hubs. Those announcements are the <c>"Dropping StreamEndedEvent … the target stream is
    /// gone"</c> lines of #2776 — and they are logged
    /// <c>SyncStreamOptions.SyncHubRegistrationGrace</c> (5 s) AFTER the stream actually ended,
    /// which is what made that issue read as a mid-run hub teardown.</para>
    ///
    /// <para><b>The real fix</b> is to make the writer's base version-aware rather than to buy its
    /// freshness with a full re-subscribe — i.e. wait for the mirror to reach the version the
    /// change feed announced. That is a change to the write path, not to this method.</para>
    /// </summary>
    private void EvictForPath(string path)
    {
        if (string.IsNullOrEmpty(path) || _remoteStreamCache.IsEmpty)
            return;

        // Do NOT unconditionally dispose the evicted stream — an undeclared reader (e.g. a
        // MeshDataSource reduce callback that handed the stream on) may still be attached and
        // needs to keep receiving updates until it drops on its own. The eviction only prevents
        // NEW callers from re-using the now-stale stream; the next GetRemoteStream creates a
        // fresh one against the (re-)activated owner. A stream whose holders DECLARED
        // themselves (see <see cref="LeaseRemoteStream"/>) and have all left is reclaimed
        // immediately — it is out of the cache, so nothing can adopt it any more.
        foreach (var key in _remoteStreamCache.Keys)
        {
            // Owner-address match only — every identity's stream for that owner is evicted.
            // 🚨 Unconditional ON PURPOSE — see the remarks above before adding a liveness gate.
            if (string.Equals(key.Owner.ToString(), path, StringComparison.OrdinalIgnoreCase)
                && _remoteStreamCache.TryRemove(key, out var removed))
            {
                // Keep ownership of the evicted stream so Dispose still tears
                // down its `sync/` hub — dropping it here orphaned the hub (never
                // disposed → TimerQueue-pinned forever). Only a materialised stream
                // has a hub to dispose.
                if (removed.IsValueCreated)
                {
                    _evictedRemoteStreams[removed.Value] = 0;
                    ReclaimIfUnheld(removed.Value);
                }
                _logger.LogDebug(
                    "Evicted remote stream cache for {Address} after change event.",
                    key.Owner);
            }
        }
    }

    // 🚨 THE LIFETIME OF AN EVICTED STREAM — declared holders, NOT Rx subscriber counts.
    //
    // A change-feed eviction takes a stream OUT of _remoteStreamCache but cannot dispose it at
    // the eviction site: it does not know whether anyone is still reading it. Parking it and
    // hoping is what leaked — every write to a subscribed path minted a fresh client `sync/` hub
    // (and, via its SubscribeRequest, a matching `sync/` hub on the owner) while every
    // predecessor sat in _evictedRemoteStreams until the process died (Systemorph/MeshWeaver#1324:
    // the parked set grew 1 → 23 monotonically over three NodeType recompiles and was never
    // drained — its only reaper is the shared mesh-node cache's idle sweep, which needs zero
    // subscribers AND ten minutes untouched, a condition a continuously-written path never meets).
    //
    // 🚨 Counting Rx subscribers CANNOT answer "is anyone still reading this": the reduce chain
    // CreateExternalClient builds subscribes to the stream ITSELF (the outbound
    // change-notification pipeline and the version-gate observer), so an evicted stream measures
    // 2–3 subscribers and never reaches zero. That approach was implemented, measured and
    // reverted — a mechanism that never triggers is worse than none.
    //
    // So holders DECLARE themselves. Everything that keeps a remote stream past the call that
    // resolved it takes a LEASE (AcquireRemoteStreamUnchecked) and releases it when it is done — the
    // shared mesh-node cache's hydration for as long as its entry lives, and each cross-hub write
    // for the duration of its Observable.Create subscription. When the last declared holder
    // leaves a stream that is already evicted, nothing can adopt it (it is out of the cache), so
    // it is disposed at once: UnsubscribeRequest goes to the owner, both `sync/` hubs die.
    //
    // A stream NOBODY leased is never in this registry and keeps the old conservative parking —
    // undeclared holders (the MeshDataSource / SyncedQueryDataSource reduce callbacks) are
    // unaffected. Opting a call site in is one line and is what makes its streams reclaimable.
    private readonly ConcurrentDictionary<ISynchronizationStream, int> _remoteStreamLeases =
        new(StreamReferenceComparer.Instance);

    /// <summary>
    /// Resolves the remote stream for (<paramref name="owner"/>, <paramref name="reference"/>)
    /// AND declares the caller as a holder of it. Dispose the returned lease when done — that is
    /// what lets an evicted stream be reclaimed instead of parked forever (see the
    /// <see cref="_remoteStreamLeases"/> note). The liveness re-check closes the window where the
    /// stream was reclaimed between resolution and the lease: a dead instance is released and the
    /// resolve retried, which builds a fresh one (the same retry contract as
    /// <see cref="GetExternalClientSynchronizationStream{TReduced,TReference}"/>).
    /// </summary>
    internal (ISynchronizationStream<TReduced> Stream, IDisposable Lease)
        AcquireRemoteStreamUnchecked<TReduced, TReference>(Address owner, TReference reference)
        where TReference : WorkspaceReference
    {
        while (true)
        {
            var stream = GetRemoteStreamUnchecked<TReduced, TReference>(owner, reference);
            // 🚨 Probe BEFORE the lease, and do NOT retry a stream that was already dead when it
            // arrived. GetRemoteStreamUnchecked returns either a stream that was usable when it
            // resolved it, or one it just built that is already dead (its own spin guard — a
            // construction-time fault is reproducible, so re-resolving only mints another corpse,
            // and repeating that here is the same unbounded spin one level up). Such a stream is
            // out of the cache and already closed, so there is nothing to declare a hold on
            // either: a lease could only pin a corpse in the bookkeeping. Hand it back with an
            // empty lease and let the caller's subscribe collect the terminal.
            if (!StreamLiveness.IsUsable(stream))
                return (stream, System.Reactive.Disposables.Disposable.Empty);

            // Live at resolve time: take the hold, then re-check. Only a reclaim landing in that
            // window makes this disagree, and only that race is worth another turn.
            var lease = LeaseRemoteStream(stream);
            if (StreamLiveness.IsUsable(stream))
                return (stream, lease);
            lease.Dispose();
        }
    }

    /// <summary>Declares a holder of <paramref name="stream"/>; disposing the returned
    /// handle releases it (idempotent).</summary>
    private IDisposable LeaseRemoteStream(ISynchronizationStream stream)
    {
        _remoteStreamLeases.AddOrUpdate(stream, 1, (_, count) => count + 1);
        // Disposable.Create runs its action AT MOST ONCE (Interlocked-swapped internally), so a
        // double-dispose of the lease handle cannot under-count the holders.
        return System.Reactive.Disposables.Disposable.Create(() =>
        {
            while (true)
            {
                // 🚨 Read-then-TryUpdate, never AddOrUpdate: DetachRemoteStreams REMOVES the
                // bookkeeping when it hands ownership out, and re-adding a zero entry here would
                // pin a stream this workspace no longer owns.
                if (!_remoteStreamLeases.TryGetValue(stream, out var current))
                    return;
                var remaining = current > 0 ? current - 1 : 0;
                if (!_remoteStreamLeases.TryUpdate(stream, remaining, current))
                    continue;
                if (remaining == 0)
                    ReclaimIfUnheld(stream);
                return;
            }
        });
    }

    /// <summary>
    /// Disposes <paramref name="stream"/> iff it is BOTH evicted (parked — no caller can adopt
    /// it) AND has no remaining declared holder. Called from the two edges that can make that
    /// true: the eviction itself, and the release of the last lease.
    /// </summary>
    private void ReclaimIfUnheld(ISynchronizationStream stream)
    {
        if (!_remoteStreamLeases.TryGetValue(stream, out var leases) || leases != 0)
            return;
        if (!_evictedRemoteStreams.TryRemove(stream, out _))
            return;
        _remoteStreamLeases.TryRemove(stream, out _);
        try
        {
            stream.Dispose();
            _logger.LogDebug(
                "Workspace {WorkspaceId} disposed superseded remote stream for {Owner} — "
                + "no declared holder remains.", Id, stream.Owner);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Workspace {WorkspaceId} error disposing superseded remote stream for {Owner}",
                Id, stream.Owner);
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
    // Hashes by REFERENCE: stream identity here IS the instance. SynchronizationStream now
    // declares reference identity itself (the generated structural GetHashCode recursed into
    // StreamConfiguration, which holds the stream back — an uncatchable StackOverflow, #2163…),
    // so this comparer is no longer load-bearing for that; it is kept because ISynchronizationStream
    // is an interface and reference semantics must hold for ANY implementation put in this set.
    private readonly ConcurrentDictionary<ISynchronizationStream, byte> _evictedRemoteStreams =
        new(StreamReferenceComparer.Instance);

    /// <summary>Reference-identity comparer for stream instances — see the
    /// <see cref="_evictedRemoteStreams"/> field note.</summary>
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
        // Ownership moves to the caller (it disposes, or hands them back via ParkRemoteStreams),
        // so drop the lease bookkeeping: keeping a zero-count entry would pin a stream this
        // workspace no longer owns. A re-parked stream simply re-enters the conservative
        // "undeclared holders" bucket until a fresh lease is taken.
        foreach (var stream in detached)
            _remoteStreamLeases.TryRemove(stream, out _);
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

            // The counterpart of the eviction line below, and the one #1324 needed: a client
            // `sync/` hub (plus its owner-side twin) is born here, so "who keeps minting mirrors
            // for this path, under which identity" is answerable from a Debug-level run instead of
            // a heap dump. Logged only on a genuine MISS — a cache hit is free and silent.
            var freshlyCreated = !lazy.IsValueCreated;

            var stream = lazy.Value;

            if (freshlyCreated)
                _logger.LogDebug(
                    "Workspace {WorkspaceId} opened remote stream {StreamId} for {Owner} as {Identity}.",
                    Id, stream.StreamId, key.Item1, key.Item3);

            // Check if the cached stream is still alive — the ONE shared predicate, so this cache
            // cannot drift from the other two (StreamLiveness explains why it is not just a
            // RunLevel probe, why the stream's source chain counts too, and why a TERMINALLY
            // FAULTED store is as dead as a disposed one).
            if (StreamLiveness.IsUsable(stream))
                return (ISynchronizationStream<TReduced>)stream;

            // Dead — remove (if still ours) and retry. The TryRemove guards
            // against the case where another thread already replaced the
            // entry: only the original Lazy is removed.
            ((ICollection<KeyValuePair<(Address Owner, WorkspaceReference Reference, string Identity), Lazy<ISynchronizationStream>>>)_remoteStreamCache)
                .Remove(new KeyValuePair<(Address Owner, WorkspaceReference Reference, string Identity), Lazy<ISynchronizationStream>>(key, lazy));

            // 🚨 A mirror that FAULTED is undisposed, so removing it from the cache is not enough:
            // it still owns a client `sync/` hub (and an owner-side twin) whose stale-callback
            // scanner roots it in the TimerQueue forever — the #1324 leak, and nothing else will
            // ever close it. Unlike the change-feed eviction, which parks a HEALTHY stream because
            // a reader may still be attached, a faulted store can never notify anyone again (Rx
            // grammar), so there is nobody left to keep it alive for. Faulted ONLY: a stream that
            // is unusable because its hub is winding down is already inside that cascade — see
            // StreamLiveness.HasFaulted for why closing that one from here would be wrong.
            if (StreamLiveness.HasFaulted(stream))
                DiscardFaultedRemoteStream(stream);

            // 🚨 A stream WE JUST BUILT that is already dead is not a stale cache entry — the
            // failure is reproducible at construction (a same-process owner NACKing our
            // SubscribeRequest inline, an absent owner answering NotFound synchronously), so
            // re-resolving is the create → fault → create spin, each turn minting a
            // SynchronizationStream and its `sync/{id}` sub-hub. Hand this one back instead: its
            // store already holds the terminal, so the caller gets the real error on subscribe
            // and the NEXT call — this entry now being out of the cache — starts clean. Same
            // reasoning, same shape, as GetStream's local-reduce guard below.
            if (freshlyCreated)
                return (ISynchronizationStream<TReduced>)stream;
        }
    }

    /// <summary>
    /// Closes a remote stream this workspace has just dropped from
    /// <see cref="_remoteStreamCache"/> because its store took a terminal error. Parks it so the
    /// lease bookkeeping owns its disposal, and disposes it at once when no holder is declared.
    /// </summary>
    /// <param name="stream">The faulted stream that was removed from the cache.</param>
    private void DiscardFaultedRemoteStream(ISynchronizationStream stream)
    {
        _evictedRemoteStreams[stream] = 0;
        if (_remoteStreamLeases.ContainsKey(stream))
        {
            // A declared holder is still mid-write against it; the last lease release reclaims.
            ReclaimIfUnheld(stream);
            return;
        }
        if (!_evictedRemoteStreams.TryRemove(stream, out _))
            return;     // another edge (eviction, a lease release, Dispose) already took it
        try
        {
            stream.Dispose();
            _logger.LogDebug(
                "Workspace {WorkspaceId} disposed faulted remote stream for {Owner}.", Id, stream.Owner);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Workspace {WorkspaceId} error disposing faulted remote stream for {Owner}", Id, stream.Owner);
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
            // Constructed BEFORE the GetOrAdd so we can tell, by identity, whether the entry we
            // ended up with is the one we just made. That single bit is what bounds the loop
            // below — see the fall-through at the end.
            var mine = new Lazy<ISynchronizationStream>(
                () => ReduceLocalStream(reference, null),
                LazyThreadSafetyMode.ExecutionAndPublication);
            var lazy = _localStreamCache.GetOrAdd(reference, mine);

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
                Remove(reference, lazy);
                throw;
            }

            // 🚨 The child's OWN hub is never enough — the cached child is its parent's SIBLING.
            // StreamLiveness.IsUsable walks the whole reduce chain; see its remarks for why, and
            // for the two failures the child-only predicate produced here (Systemorph/MeshWeaver#1455).
            if (StreamLiveness.IsUsable(stream))
                return (ISynchronizationStream<TReduced>)stream;

            Remove(reference, lazy);

            // 🚨 A FRESH reduce that is ALREADY unusable means the SOURCE is gone, not that the
            // cache entry went stale — and re-reducing can only mint another corpse. Retrying was
            // an unbounded spin, each turn allocating a SynchronizationStream and its `sync/{id}`
            // sub-hub; on a hub action block that is a permanent wedge plus a hub-allocation
            // storm. Hand this one back instead: it is the plain, uncached reduce, which is
            // exactly what a disposed source has always produced here and what ReduceShared falls
            // through to for the same reason (SynchronizationStream.ReduceShared's parent guard).
            // Its store is already completed, so a reader gets a terminal instead of a stale
            // replay followed by eternal silence.
            if (ReferenceEquals(lazy, mine))
                return (ISynchronizationStream<TReduced>)stream;

            // We lost the add race to another caller's entry and it was dead: drop it and let the
            // next turn install ours. Every turn removes one entry, so this cannot spin.
        }

        void Remove(WorkspaceReference key, Lazy<ISynchronizationStream> entry) =>
            ((ICollection<KeyValuePair<WorkspaceReference, Lazy<ISynchronizationStream>>>)_localStreamCache)
                .Remove(new KeyValuePair<WorkspaceReference, Lazy<ISynchronizationStream>>(key, entry));
    }

    /// <summary>
    /// 🚨 The failure NAMES the reference and the owning hub. It used to throw a bare
    /// <c>InvalidOperationException("Failed to create stream")</c> — no reference, no owner, no
    /// inner cause — and that is exactly why three deliberate fault classifiers in
    /// <c>SubscribeWithReEstablish</c> all missed it and reported a routine boot-time probe as a
    /// transient prod ERROR (Systemorph/MeshWeaver#2990). A diagnostic that cannot be classified
    /// costs more than the line it saves.
    /// </summary>
    private ISynchronizationStream<TReduced> ReduceLocalStream<TReduced>(
        WorkspaceReference<TReduced> reference,
        Func<StreamConfiguration<TReduced>, StreamConfiguration<TReduced>>? configuration)
        => (ISynchronizationStream<TReduced>?)ReduceManager.ReduceStream(
               this,
               reference,
               configuration
           ) ?? throw new InvalidOperationException(
               $"Failed to create stream for {reference} on {Hub.Address}: the workspace's "
               + $"ReduceManager has no reducer producing {typeof(TReduced).Name} from this "
               + "reference, or the data source it would reduce from has not been started.");

    /// <inheritdoc />
    /// <remarks>Same diagnostic contract as <see cref="ReduceLocalStream{TReduced}"/>: the failure
    /// names the collections and the owning hub, never a bare "Failed to create stream".</remarks>
    public ISynchronizationStream<EntityStore> GetStream(params Type[] types)
    {
        // 🚨 `name is not null` is not belt-and-braces — it is what makes this a `string[]`.
        // TryGetCollectionName's out parameter is `string?`, so without it the projection is
        // `string?` and the array is `string?[]`, which CollectionsReference (an
        // IReadOnlyCollection<string>) rejects with CS8620 — an error under CI's -warnaserror.
        // Checking the invariant is the honest way to satisfy that; a `!` suppression would only
        // assert it, and would let a `true` with a null name through as a null collection name.
        var collections = types
            .Select(t =>
                DataContext.TypeRegistry.TryGetCollectionName(t, out var name) && name is not null
                    ? name
                    : throw new ArgumentException($"Type {t.FullName} is unknown.")
            ).ToArray();
        return (ISynchronizationStream<EntityStore>?)
            ReduceManager.ReduceStream<EntityStore>(this, new CollectionsReference(collections), x => x)
            ?? throw new InvalidOperationException(
                $"Failed to create stream for collections [{string.Join(", ", collections)}] on "
                + $"{Hub.Address}: the workspace's ReduceManager has no reducer producing an "
                + "EntityStore from a CollectionsReference, or the data source it would reduce "
                + "from has not been started.");
    }

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
        // Nothing left to reclaim — drop the holder bookkeeping so it stops referencing streams.
        _remoteStreamLeases.Clear();

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
    private sealed class ClientSubscription(
        Address subscriber, WorkspaceReference reference, ISynchronizationStream stream)
    {
        /// <summary>
        /// The subscriber's ADDRESS, kept alongside the string that keys the dictionary: a recycle
        /// announcement has to post back to it, and an Address cannot be reconstructed faithfully
        /// from its own ToString() (a routed target can carry a Host qualifier).
        /// </summary>
        public Address Subscriber { get; } = subscriber;
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
    /// Total client subscriptions this workspace evicted on an authoritative
    /// <see cref="DeliveryFailure.TargetUnserved"/> verdict — see
    /// <see cref="EvictClientSubscriptions"/>. Read by tests to make "the eviction ran" a
    /// positive signal, so the absence of a registry entry can never be mistaken for one.
    /// </summary>
    private int clientSubscriptionsEvicted;

    /// <inheritdoc cref="clientSubscriptionsEvicted"/>
    internal int ClientSubscriptionsEvicted => Volatile.Read(ref clientSubscriptionsEvicted);

    /// <summary>
    /// 🚨 Disposes every server-side stream this owner still serves for
    /// <paramref name="subscriberPath"/> — the OWNER-SIDE half of the dead-subscriber delivery
    /// storm fix (issues #2426/#2546).
    ///
    /// <para>The registry's own comment (see <see cref="_clientSubscriptions"/>) names the leak:
    /// "only an UnsubscribeRequest disposes a server-side stream", and a subscriber whose PROCESS
    /// dies — a restarted portal's circuits, a disconnected gRPC participant — never sends one.
    /// The owner then fans every change out to the corpse forever; the router refuses each
    /// delivery ("no live subscriber") and answers with a <see cref="DeliveryFailure"/> whose
    /// <see cref="DeliveryFailure.TargetUnserved"/> stamp is the authoritative "that address is
    /// dead" verdict. This method is what that verdict finally reaches: it disposes each matching
    /// stream through its own sync hub — the exact route an <c>UnsubscribeRequest</c> takes — and
    /// the stream's disposal registration removes the registry entry pair-exact.</para>
    ///
    /// <para>Evict-only, like every recovery in this codebase: nothing here retries, resubscribes
    /// or polls. A subscriber that was NOT actually dead (its stream subscription flapped) loses
    /// only its server-side stream — its own change-feed latch resubscribes and the owner builds a
    /// fresh one, exactly as after an owner recycle.</para>
    ///
    /// <para>The registry entry is removed HERE, before the hub is disposed, not left to the
    /// stream's own disposal registration. Hub disposal is asynchronous and the sync hub's
    /// <c>RunLevel</c> stays <c>Started</c> for a moment after <c>Dispose()</c> returns, so a
    /// resubscribe landing in that window would pass <see cref="GetClientSubscription"/>'s
    /// liveness probe and be re-asserted onto a stream that is being torn down — served off a
    /// corpse instead of getting a fresh stream. Pair-exact (the <c>KeyValuePair</c> overload
    /// removes only this exact entry), so a later stream that legitimately took the key over is
    /// never unregistered or disposed by this pass — its own fan-out earns its own verdict.</para>
    /// </summary>
    /// <param name="subscriberPath">The dead subscriber's address, as
    /// <see cref="Address.ToString"/> renders it (the registry's key form).</param>
    /// <returns>How many streams were disposed.</returns>
    internal int EvictClientSubscriptions(string subscriberPath)
    {
        var evicted = 0;
        foreach (var kv in _clientSubscriptions)
        {
            if (!string.Equals(kv.Key.Subscriber, subscriberPath, StringComparison.Ordinal))
                continue;
            // Unregister first, pair-exact — see the remarks. A miss means another pass (or the
            // stream's own disposal) already took this entry; nothing of ours is left to dispose.
            if (!_clientSubscriptions.TryRemove(kv))
                continue;
            try
            {
                // The same disposal route WithHandler<UnsubscribeRequest> takes: the per-stream
                // sync hub (SynchronizationStream assigns its own sync/{clientId} sub-hub to
                // .Hub — never the owner hub). The stream's own disposal registration then finds
                // the entry already gone and does nothing.
                kv.Value.Stream.Hub.Dispose();
                evicted++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Workspace {WorkspaceId}: failed to dispose server-side stream {StreamId} "
                    + "for unserved subscriber {Subscriber}", Id, kv.Key.StreamId, subscriberPath);
            }
        }
        if (evicted > 0)
            Interlocked.Add(ref clientSubscriptionsEvicted, evicted);
        return evicted;
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
        _clientSubscriptions[key] = new ClientSubscription(subscriber, reference, stream);
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
