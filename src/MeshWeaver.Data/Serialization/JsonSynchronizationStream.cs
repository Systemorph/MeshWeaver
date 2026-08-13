using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Json;
using MeshWeaver.Domain;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Data.Serialization;

/// <summary>
/// Thrown by <c>ApplyAdd</c>/<c>ApplyReplace</c>/<c>ApplyRemove</c> when a patch's array
/// index doesn't match the locally cached snapshot (drift between the owner's cached
/// JSON view and the authoritative entity store). The upstream <c>ToDataChanged</c>
/// catches this and falls back to emitting a <see cref="ChangeType.Full"/> snapshot
/// so subscribers can resync.
/// </summary>
public sealed class StaleStreamStateException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="StaleStreamStateException"/> class.
    /// </summary>
    /// <param name="message">A message describing the drift that was detected.</param>
    public StaleStreamStateException(string message) : base(message) { }
}

/// <summary>
/// JSON-patch helpers that translate between an entity store's JSON projection and typed
/// <see cref="ChangeItem{TStream}"/>/<see cref="EntityUpdate"/> changes, including RFC 6901 pointer
/// encoding/decoding used to address entities by collection and id.
/// </summary>
public static class JsonSynchronizationStream
{
    /// <summary>
    /// How many times one stream re-arms after the owner rejected its SubscribeRequest for being
    /// mid-recycle. A recycle answers within milliseconds and the re-ask activates the fresh hub,
    /// so the healthy case needs one; the bound exists purely so an owner stuck in a recycle LOOP
    /// degrades to the pre-existing orphaned-stream behaviour rather than becoming a resubscribe
    /// storm. It is not a budget to widen — if a stream needs more than this, the owner is
    /// recycling pathologically and THAT is the bug.
    /// </summary>
    private const int MaxRecycleReArms = 3;

    /// <summary>
    /// How far up the hub tree the recycle re-arm looks for the local instance of a rejecting
    /// owner (see <c>LocalInstanceOf</c>). Real trees are two or three deep — mesh → cache/client
    /// → sync — so this is only a guard against a malformed parent chain.
    /// </summary>
    private const int MaxHostLookupDepth = 8;

    // Mirror of MeshWeaver.Mesh.Security.WellKnownUsers.System — Data sits below
    // Mesh.Contract in the project graph and cannot reference it. Same literal
    // recognized by AccessService.ImpersonateAsSystem and PostgreSqlMeshQuery's
    // System-bypass short-circuit.
    private const string SystemUserId = "system-security";

    /// <summary>
    /// When the FIRST heartbeat fires on a fresh subscription. The rest keep the configured
    /// <see cref="SyncStreamOptions.HeartbeatInterval"/> cadence.
    ///
    /// <para>🚨 This number is the fix for the memex 2026-07-27 outage. A stream whose owner acked
    /// the subscribe and then went quiet delivered nothing until the first heartbeat poked it —
    /// 45.20s of every compile, measured, against 2.83s of Roslyn. Starting the heartbeat early
    /// turns that into a few seconds without inventing a new mechanism: it is the same
    /// fire-and-forget [SystemMessage] the stream already sends forever, just not withheld for a
    /// full interval first.</para>
    ///
    /// <para>Deliberately NOT a re-subscribe: each SubscribeRequest creates a
    /// <c>sync/{ClientId}</c> hub on the owner's single-threaded action block, so a re-subscribe
    /// probe becomes a storm under mass cold start (every deploy) — the failure
    /// <c>ChangeFeedResubscribeCoalesceTest</c> guards, and one an earlier version of this fix hit
    /// on CI. A heartbeat costs one message and creates nothing.</para>
    /// </summary>
    /// <summary>Fallback used only when no <see cref="SyncStreamOptions"/> is registered (bare
    /// Data-layer hosts). The configured <see cref="SyncStreamOptions.FirstHeartbeat"/> wins.</summary>
    internal static readonly TimeSpan FirstHeartbeat = TimeSpan.FromSeconds(5);

    // Hub-shaped principals leak from the workspace emission scheduler when an
    // upstream notification fires under a hub's own AsyncLocal (e.g. a `sync/{guid}`
    // inner sync hub stamped during its own initialization). Those addresses
    // have no AccessAssignment → owner RLS denies for them with
    // "user 'sync/…' lacks Read". When we detect one, we fall back to System
    // (real infrastructure context with whitelisted Permission.All) instead of
    // forwarding the hub-shape identity to the owner.
    //
    // ⚠️  Kept narrow on purpose: we MUST forward a real user identity through
    // (`rbuergi`, an Entra OID GUID, an email, etc.) so the owner's per-user
    // RLS gates correctly. This helper only neutralizes principals that are
    // clearly mesh-internal hub addresses.
    private static bool LooksLikeHubPrincipal(string objectId) =>
        objectId.StartsWith("sync/", StringComparison.OrdinalIgnoreCase)
        || objectId.StartsWith("mesh/", StringComparison.OrdinalIgnoreCase)
        || objectId.StartsWith("node/", StringComparison.OrdinalIgnoreCase)
        || objectId.StartsWith("activity/", StringComparison.OrdinalIgnoreCase)
        || objectId.StartsWith("portal/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The identity a remote subscribe runs under, resolved from the ambient
    /// <see cref="AccessService"/> context. <see cref="Id"/> is the value stamped on
    /// <c>SubscribeRequest.Identity</c>; <see cref="RealUser"/> is the captured context to
    /// re-apply around the post (null ⇒ the subscribe runs as System).
    /// </summary>
    internal readonly record struct RemoteSubscribeIdentity(string Id, AccessContext? RealUser)
    {
        /// <summary>True when a genuine end-user identity was resolved (not the System fallback).</summary>
        public bool IsRealUser => RealUser is not null;
    }

    /// <summary>
    /// Resolves the identity that <see cref="CreateExternalClient{TReduced,TReference}"/> will
    /// stamp on its <c>SubscribeRequest</c>.
    ///
    /// <para>🚨 This is ALSO the identity component of <c>Workspace._remoteStreamCache</c>'s key.
    /// The owner applies its RLS gate ONCE, at subscribe time, for exactly this identity — so the
    /// resulting stream carries that identity's permission view for its whole life. Cache key and
    /// subscribe identity must therefore be computed from the SAME function: if they ever drift,
    /// one identity's stream is served to another, which is a cross-user information disclosure
    /// (and, in the other ordering, a refused stream handed to a permitted reader whose view then
    /// renders empty). Both directions are pinned by
    /// <c>MeshWeaver.Security.Test/RemoteStreamCacheIdentityTest</c>.</para>
    ///
    /// <para>Prefer the REAL user when the ambient AccessContext identifies one (the typical
    /// Blazor-circuit / API-token path). Fall back to System ONLY when AsyncLocal carries no user
    /// — empty, or a hub-shaped principal (<c>sync/</c>, <c>mesh/</c>, <c>node/</c>, …) that leaked
    /// from a workspace emission scheduler.</para>
    /// </summary>
    internal static RemoteSubscribeIdentity ResolveSubscribeIdentity(AccessService? accessService)
    {
        var ambient = accessService?.Context ?? accessService?.CircuitContext;
        var isRealUser = ambient is not null
            && !string.IsNullOrEmpty(ambient.ObjectId)
            && !LooksLikeHubPrincipal(ambient.ObjectId);
        return isRealUser
            ? new RemoteSubscribeIdentity(ambient!.ObjectId!, ambient)
            : new RemoteSubscribeIdentity(SystemUserId, null);
    }

    private static ILogger GetLogger(IServiceProvider serviceProvider)
    {
        try
        {
            return serviceProvider.GetService<ILoggerFactory>()
                       ?.CreateLogger(typeof(JsonSynchronizationStream))
                   ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance.CreateLogger(typeof(JsonSynchronizationStream));
        }
        catch (ObjectDisposedException)
        {
            return Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance.CreateLogger(typeof(JsonSynchronizationStream));
        }
    }

    /// <summary>Lock-free monotonic max — keeps the highest observed version under concurrent writers.</summary>
    private static void InterlockedMax(ref long location, long value)
    {
        var current = System.Threading.Interlocked.Read(ref location);
        while (value > current)
        {
            var prior = System.Threading.Interlocked.CompareExchange(ref location, value, current);
            if (prior == current) return;
            current = prior;
        }
    }

    /// <summary>
    /// Subscribes to the mesh change feed (resolved via reflection to avoid a
    /// Data → Mesh.Contract → Layout → Data project cycle) and invokes
    /// <paramref name="onOwnerChanged"/> with the announced node version when an event's Path
    /// equals the owner's bare path. Returns null if no change-feed service is registered.
    /// </summary>
    private static IDisposable? TrySubscribeOwnerPathChangeFeed(
        IServiceProvider serviceProvider, ILogger logger, string ownerPath, Action<long> onOwnerChanged)
    {
        try
        {
            var feedType = Type.GetType(
                "MeshWeaver.Mesh.Services.IMeshChangeFeed, MeshWeaver.Mesh.Contract",
                throwOnError: false);
            if (feedType is null) return null;
            var feed = serviceProvider.GetService(feedType);
            if (feed is null) return null;

            var eventType = Type.GetType(
                "MeshWeaver.Mesh.Services.MeshChangeEvent, MeshWeaver.Mesh.Contract",
                throwOnError: false);
            if (eventType is null) return null;
            var pathProp = eventType.GetProperty("Path");
            if (pathProp is null) return null;
            var versionProp = eventType.GetProperty("Version");

            var helper = typeof(JsonSynchronizationStream).GetMethod(
                nameof(SubscribeOwnerPathChangeFeedHelper),
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .MakeGenericMethod(eventType);
            return (IDisposable?)helper.Invoke(null, [feed, pathProp, versionProp, ownerPath, onOwnerChanged]);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex,
                "Stream subscriber could not attach MeshChangeFeed listener for {Owner} — falling back to heartbeat-only resubscribe.",
                ownerPath);
            return null;
        }
    }

    private static IDisposable? SubscribeOwnerPathChangeFeedHelper<TEvent>(
        object feed, System.Reflection.PropertyInfo pathProperty,
        System.Reflection.PropertyInfo? versionProperty, string ownerPath, Action<long> onOwnerChanged)
        where TEvent : class
    {
        Action<TEvent> handler = evt =>
        {
            try
            {
                if (pathProperty.GetValue(evt) is string p
                    && string.Equals(p, ownerPath, StringComparison.OrdinalIgnoreCase))
                    // Announce the node version the event carries (0 when unavailable) so the
                    // subscriber can decide whether it already RECEIVED that write through its
                    // own subscription — see the version-gated staleness check in
                    // CreateExternalClient.
                    onOwnerChanged(versionProperty?.GetValue(evt) is long v ? v : 0L);
            }
            catch { /* keep change-feed alive on handler faults */ }
        };
        var subscribe = feed.GetType().GetMethod("Subscribe");
        return (IDisposable?)subscribe!.Invoke(feed, [handler, null]);
    }

    internal static ISynchronizationStream CreateExternalClient<TReduced, TReference>(
        this IWorkspace workspace,
        Address owner,
        TReference reference,
        bool impersonateAsHub = false
    )
    where TReference : WorkspaceReference
    {
        var hub = workspace.Hub;
        // Typed since 2026-07-27: same condition, same ObjectDisposedException base (so the
        // Blazor circuit's `catch (ObjectDisposedException)` around BindStream still applies),
        // but now CLASSIFIABLE as the transient ErrorType.ShuttingDown when it escapes a
        // handler — the sibling refusal in SynchronizationStream's constructor throws the same
        // type, so "the host is going down, ask again" has ONE shape across stream creation.
        if (hub.RunLevel > MessageHubRunLevel.Started)
            throw new HubDisposingException(hub.Address, reference);

        var logger = GetLogger(hub.ServiceProvider);
        // link to deserialized world. Will also potentially link to workspace.
        var partition = reference is IPartitionedWorkspaceReference p ? p.Partition : null;

        // Capture the caller's AccessContext at stream-creation time. The internal
        // change-notification Subscribe (below) fires on the stream's emission scheduler,
        // where AsyncLocal AccessContext does NOT match the user who created this stream
        // — it would default to the per-cell hub's impersonated address and trigger
        // "Access denied" on the owner. Stamping each post with the captured user
        // context preserves authorship through the Update path.
        var accessServiceForCapture = hub.ServiceProvider.GetService<AccessService>();
        var capturedAccessContext = accessServiceForCapture?.Context;

        var reduced = new SynchronizationStream<TReduced>(
                new(owner, partition),
                hub,
                reference,
                workspace.ReduceManager.ReduceTo<TReduced>(),
                config => config
                    .WithClientId(config.Stream.StreamId)
            );


        if (typeof(TReduced) == typeof(JsonElement))
            reduced.RegisterForDisposal(
                reduced
                    .ToDataChanged<TReduced, PatchDataChangeRequest>(c => reduced.ClientId.Equals(c.ChangedBy))
                    .Synchronize()
                    .Where(x => x is not null)
                    .Subscribe(e =>
                    {
                        logger.LogDebug("Stream {streamId} sending change notification to owner {owner}",
                            reduced.StreamId, reduced.Owner);
                        hub.Post(e, o =>
                        {
                            var opts = o.WithTarget(reduced.Owner);
                            return capturedAccessContext != null ? opts.WithAccessContext(capturedAccessContext) : opts;
                        });
                    },
                    ex => logger.LogDebug(ex, "Stream {streamId} errored", reduced.StreamId))
            );

        else if (!owner.Equals(hub.Address))
            reduced.RegisterForDisposal(
                reduced
                    .ToDataChangeRequest(c => reduced.ClientId.Equals(c.StreamId))
                    .Synchronize()
                    .Where(x => x.Creations.Any() || x.Deletions.Any() || x.Updates.Any())
                    .Subscribe(e =>
                    {
                        logger.LogDebug("Stream {streamId} sending change notification to owner {owner}",
                            reduced.StreamId, reduced.Owner);
                        e = e with { ClientId = reduced.StreamId };
                        var delivery = hub.Post(e, o =>
                        {
                            var opts = o.WithTarget(reduced.Owner);
                            return capturedAccessContext != null ? opts.WithAccessContext(capturedAccessContext) : opts;
                        });
                        if (delivery != null)
                        {
                            hub.Observe(delivery)
                                .Subscribe(
                                    response =>
                                    {
                                        if (response.Message is DataChangeResponse { Status: DataChangeStatus.Failed } failed)
                                        {
                                            var reason = DescribeFailure(failed.Log);
                                            logger.LogError("Stream {streamId} DataChangeRequest failed: {Error}",
                                                reduced.StreamId, reason);
                                            reduced.OnError(new InvalidOperationException(
                                                $"DataChangeRequest failed for stream {reduced.StreamId}: {reason}"));
                                        }
                                    },
                                    ex =>
                                    {
                                        logger.LogError(ex, "Stream {streamId} DataChangeRequest failed",
                                            reduced.StreamId);
                                        reduced.OnError(new InvalidOperationException(
                                            $"DataChangeRequest failed for stream {reduced.StreamId}", ex));
                                    });
                        }
                    },
                    ex => logger.LogDebug(ex, "Stream {streamId} errored", reduced.StreamId))
            );


        var accessService = hub.ServiceProvider.GetService<AccessService>();
        // Post from `hub` (outer hub), NOT `reduced.Hub` (inner sync hub).
        // When the outer hub is the mesh hub, HierarchicalRouting skips sender-wrapping
        // (parentHub.Address.Type == MeshType check), so posting from the inner hub
        // would leave a bare `sync/{id}` subscriber. The owner hub then finds its OWN
        // local inner hub at that address and delivers DataChangedEvent locally instead of
        // routing it back. Posting from `hub` makes hub.Address the Subscriber, so
        // RouteStreamMessage on the outer hub correctly routes to the inner sync hub.
        //
        // Use hub.Observe(object, opts) — the register-before-post overload — to avoid
        // the race where the owner responds (first DataChangedEvent with ResponseFor) before
        // hub.Observe(delivery) registers the subject in responseSubjects. The owner side
        // sends the first DataChangedEvent as ResponseFor(subscribeDelivery), which causes
        // HandleCallbacks to fire and close the responseSubjects entry cleanly. Subsequent
        // DataChangedEvents flow through RouteStreamMessage as normal.
        //
        // 🚨 Identity selection: prefer the REAL user when the ambient AccessContext
        // identifies one (the typical Blazor-circuit / API-token path — middleware
        // set Context to the caller's identity before this Subscribe). Fall back to
        // System ONLY when AsyncLocal carries no user — empty, anonymous, or a
        // hub-shaped principal (`sync/`, `mesh/`, `node/`, `activity/`, …) that
        // leaked from a workspace emission scheduler. The earlier blanket
        // ImpersonateAsSystem here (88764f803) collapsed every Blazor LayoutArea
        // subscription onto System, so the User-node hub's Activity area saw
        // `system-security` instead of `rbuergi` and rendered the visitor profile
        // for the page owner. Per-user identity must flow into the SubscribeRequest
        // so the owner's RLS can enforce per-user reads; System fallback exists
        // only for the bare-infrastructure paths where no user identity exists.
        // 🚨 Resolved through the SHARED helper so this identity is bit-for-bit the one
        // Workspace._remoteStreamCache keyed this stream under — see ResolveSubscribeIdentity.
        var subscribeIdentity = ResolveSubscribeIdentity(accessService);
        var ambient = subscribeIdentity.RealUser;
        var isRealUser = subscribeIdentity.IsRealUser;
        var identityForSubscribe = subscribeIdentity.Id;

        // Keep-alive machinery (the 45s heartbeat + the change-feed resubscribe) for a
        // REMOTE owner is collected here so a TERMINAL failure of the initial
        // SubscribeRequest — the owner ADDRESS DOES NOT EXIST (DeliveryFailure / NotFound) —
        // can tear it ALL down. Without this, reduced.OnError only faults the SUBSCRIBERS;
        // the stream object lingers "errored but undisposed", and the heartbeat (registered
        // on the stream's DISPOSAL, not its error) keeps posting HeartBeatEvent to the
        // non-existent owner FOREVER → "[ROUTE] NotFound" every heartbeat interval, one
        // zombie subscription per open. Multiplied by Blazor re-render / per-cell fan-out
        // that re-opens absent paths, that ramps into the resubscribe storm that pins the
        // CPU. A terminal NotFound must STOP (the consumer re-asks if the node later appears,
        // ideally via an empty-on-absent query) — it must never auto-heartbeat/resubscribe.
        // CompositeDisposable is race-free: anything Add()ed after it is disposed is disposed
        // immediately, so the async onError tearing it down can't lose to the synchronous
        // heartbeat setup below (or vice-versa).
        var keepAlive = new System.Reactive.Disposables.CompositeDisposable();
        reduced.RegisterForDisposal(keepAlive);
        // Initial SubscribeRequest — surface a terminal NotFound, nothing more.
        //   • NO retry/resubscribe loop — a watchdog that re-posts SubscribeRequest is the forbidden
        //     band-aid that stormed prod 2026-06-08.
        //   • NO aggressive per-attempt timeout — a 20ms cap faulted legitimate slow / cold-activating
        //     owners (a real per-node-hub subscribe needs SECONDS), which broke data sync across the
        //     whole portal (CI 7→61). We don't race the owner; it replies when it's ready.
        //   • NO caching of the not-found — a later real subscribe re-asks fresh.
        // The owner replies with the first DataChangedEvent (already forwarded to the inner sync hub
        // by RouteStreamMessage); a DeliveryFailure / NotFound — the owner ADDRESS DOES NOT EXIST —
        // surfaces as OnError, so we fault subscribers and dispose the keep-alive so nothing
        // heartbeats/resubscribes a non-existent owner. Reactive end-to-end: no await.
        //
        // 🚨 Carries the "the owner rejected us because it is recycling" signal from the subscribe
        // arm below to the re-arm wired further down (where Resubscribe is in scope).
        //
        // REPLAY, not a bare Subject. The rejection normally arrives long after both ends are
        // wired, but a same-process owner can NACK inside the subscribe call itself — before this
        // method reaches the re-arm — and a bare Subject drops an emission that has no subscriber
        // yet. That would resurrect the orphaned stream in exactly the fast local case this fix
        // exists for, and only sometimes: the classic invisible race.
        var rejectedByRecycle = new System.Reactive.Subjects.ReplaySubject<string>(1);
        var observeSubscription = Observable
            .Using(
                // Apply an explicit identity SCOPE for the post — do NOT rely on the ambient AsyncLocal
                // still being set. Observable.Using's factory runs at SUBSCRIBE time, and a sync
                // re-subscribe / keep-alive / Blazor re-render fan-out fires on a background Rx
                // scheduler where AsyncLocal is WIPED. Previously a real user applied no scope and
                // trusted AsyncLocal: when it was wiped the SubscribeRequest delivery got a NULL
                // AccessContext → PostPipeline fail-closed → owner DENIES the subscribe → the consumer
                // re-opens it → flood of denied SubscribeRequests that wedged the Space hub
                // (AgenticPension). Re-apply the CAPTURED user (SwitchAccessContext(ambient)) so the
                // post carries the right identity regardless of thread; non-user → System. Either way
                // AccessContext is NEVER null, so the subscribe is authorised (RLS still gates the
                // user's actual Read) instead of fail-closed-denied.
                () => (isRealUser ? accessService?.SwitchAccessContext(ambient) : accessService?.ImpersonateAsSystem())
                      ?? (IDisposable)System.Reactive.Disposables.Disposable.Empty,
                _ => hub.Observe(
                        new SubscribeRequest(reduced.StreamId, reference) { Identity = identityForSubscribe },
                        o => impersonateAsHub ? o.WithTarget(owner).ImpersonateAsHub(hub.Address) : o.WithTarget(owner))
                    .Take(1))
            .Subscribe(
                _ =>
                    // The owner sends the first DataChangedEvent as the response; it is already
                    // forwarded to the inner sync hub by RouteStreamMessage. Just acknowledge.
                    logger.LogDebug("SubscribeRequest for stream {StreamId} acknowledged by owner",
                        reduced.StreamId),
                ex =>
                {
                    // 🚨 TRANSIENT shutdown reject (ErrorType.ShuttingDown): our SubscribeRequest
                    // raced the owner hub's DisposeRequest — a recycle / restart in flight, NOT a
                    // missing owner. The owner's fresh activation re-announces via the change feed
                    // and the resubscribe latch below re-sends the SubscribeRequest (~2s later in
                    // the recycle flow), so the ONLY correct reaction is to keep the stream and
                    // the keep-alive ALIVE and wait for that rehydration — exactly what happened
                    // under the pre-NACK silent drop. Faulting here killed the latch with the
                    // stream and wedged every reader of a mid-recycle NodeType to its timeout
                    // (CI 30003419841, NodeTypeCompileParkTest.RecycleRetry). This is not a
                    // retry/watchdog: nothing re-posts on a timer — recovery stays event-driven
                    // (change-feed announce → latch), the same protocol as an owner restart.
                    if (ex is DeliveryFailureException { Failure.ErrorType: ErrorType.ShuttingDown })
                    {
                        logger.LogDebug(
                            "SubscribeRequest for stream {StreamId} rejected — owner {Owner} is shutting down "
                            + "(recycle/restart in flight); keeping the stream alive and re-arming",
                            reduced.StreamId, owner);
                        // 🚨 …and RE-ARM, because the latch alone cannot recover this. The latch's
                        // only trigger is a mesh change-feed event on the owner's path, and a pure
                        // RECYCLE writes nothing — so a subscriber the recycle rejected waits for
                        // an announce that never comes. That hole was invisible while this
                        // rejection was silently swallowed (the sender just timed out and the
                        // consumer tore down); once the owner started answering honestly, the
                        // orphaned stream became the visible symptom — a freshly installed root
                        // whose area never renders (StaleStampRootBindingTest, 120 s with the
                        // client sitting idle after the NACK landed).
                        rejectedByRecycle.OnNext("rejected our SubscribeRequest while shutting down");
                        return;
                    }

                    // DeliveryFailure / NotFound — the owner address does not exist. Terminal: fault
                    // subscribers + tear down the keep-alive so we NEVER heartbeat or resubscribe a
                    // non-existent owner (the storm that wedges the session).
                    // Debug, NOT Warning: this is the recoverable "subscribed to an absent path" case
                    // (the consumer re-asks if the node later appears), and it is exactly the path that
                    // can fire frequently under Blazor re-render fan-out — a Warning here ships to App
                    // Insights on every miss and bleeds ingest budget. The fault below already surfaces
                    // the error to the subscriber (the GUI renders it); this line is only diagnostics.
                    logger.LogDebug(
                        "SubscribeRequest for stream {StreamId} failed — owner {Owner} unreachable: {Message}",
                        reduced.StreamId, owner, ex.Message);
                    reduced.OnError(ex);
                    keepAlive.Dispose();
                });
        reduced.RegisterForDisposal(observeSubscription);
        // Belt-and-suspenders: dispose the subscription when the HUB tears down too (idempotent).
        hub.RegisterForDisposal(observeSubscription);

        reduced.RegisterForDisposal(
            reduced.Hub.Register<UnsubscribeRequest>(
                delivery =>
                {
                    reduced.DeliverMessage(delivery);
                    return delivery.Forwarded();
                },
                d => reduced.StreamId.Equals(d.Message.StreamId)
            )
        );

        reduced.RegisterForDisposal(
            new AnonymousDisposable(
                () => hub.Post(new UnsubscribeRequest(reduced.StreamId), o => o.WithTarget(owner))
            )
        );


        // Keep the remote owner grain alive while this subscription exists.
        // HeartBeatEvent is fire-and-forget — HandleHeartBeat returns Processed() but
        // posts no response, so subscribing to a response would always time out and
        // mis-trigger Resubscribe. Recycle/recreate detection runs through the mesh
        // change feed below instead.
        if (!owner.Equals(hub.Address))
        {
            var resubscribing = 0;

            void Resubscribe(string reason)
            {
                if (Interlocked.Exchange(ref resubscribing, 1) != 0) return;

                logger.LogInformation(
                    "Stream {StreamId}: owner {Owner} {Reason} — resubscribing for fresh snapshot.",
                    reduced.StreamId, owner, reason);

                // 🚨 #325 symptom-2: this resubscribe fired because the mirror is DEMONSTRABLY behind
                // (the version gate below only lets it through when receivedVersion < announcedVersion).
                // Arm the mirror to ACCEPT the owner's fresh re-snapshot even if the (idle-recycled)
                // owner's reset Hub.Version stamps it with a frame version BELOW what the mirror cached
                // — otherwise the monotonicity guard drops it and the mirror stays orphaned. Armed
                // BEFORE the SubscribeRequest so the response Full (which can arrive fast) finds it set.
                reduced.ExpectResubscribeFull();

                // Resubscribe is INFRASTRUCTURE (cache refresh after owner restart).
                // The triggering event lands on the workspace's emission scheduler
                // where AsyncLocal AccessContext is whatever was set when the
                // upstream change was published — often a `sync/<streamId>` hub
                // address from the inner sync hub's own startup impersonation.
                // Stamping a sync hub as principal on the owner-side RLS check
                // produces "Access denied: user 'sync/…' lacks Read" because no
                // AccessAssignment exists for sync hub addresses. Same rule as
                // the MeshNodeStreamCache and the stale-patch refresh in
                // SynchronizationStream: resubscribes run as System; per-user
                // enforcement happens at the consumer layer (cache.GetStream,
                // application handlers), not at the sync-stream seam.
                using (accessService?.ImpersonateAsSystem())
                {
                    // Use register-before-post overload to avoid the race where the owner
                    // responds before the subject is registered in responseSubjects.
                    hub.Observe(
                            new SubscribeRequest(reduced.StreamId, reference) { Identity = SystemUserId },
                            o => impersonateAsHub
                                ? o.WithTarget(owner).ImpersonateAsHub(hub.Address)
                                : o.WithTarget(owner))
                        .Subscribe(
                            _ =>
                            {
                                // Owner's first DataChangedEvent is already routed to the
                                // inner hub by RouteStreamMessage; just clear the flag.
                                Interlocked.Exchange(ref resubscribing, 0);
                            },
                            ex =>
                            {
                                logger.LogWarning(ex,
                                    "Stream {StreamId}: resubscribe failed.",
                                    reduced.StreamId);
                                Interlocked.Exchange(ref resubscribing, 0);
                                // A re-ask that itself raced the SAME recycle window gets the same
                                // transient ShuttingDown verdict — push it back through the re-arm
                                // carrier so the next bounded attempt fires (shared MaxRecycleReArms
                                // budget; measured in StaleStampRootBindingTest, where the first
                                // re-ask can land while the teardown is still draining).
                                if (ex is DeliveryFailureException { Failure.ErrorType: ErrorType.ShuttingDown })
                                    rejectedByRecycle.OnNext(
                                        "re-ask was itself rejected while the owner was still shutting down");
                            });
                }
            }

            // 🚨 THE UN-ANNOUNCED RECYCLE. The change-feed latch below is the recycled-owner
            // detector, but it can only fire on a mesh change event for the owner's path — and a
            // recycle IS NOT A WRITE. Recycle the owner while a SubscribeRequest is in flight and
            // that subscriber is orphaned until something unrelated happens to write the node:
            // no announce, no event, no recovery. The heartbeat cannot rescue it either — it is
            // fire-and-forget and deliberately does not resubscribe.
            //
            // So the rejection itself is the event. The owner told us, in the same breath, both
            // that it is going and that it is coming back ("the address may reactivate; retry to
            // get the authoritative answer").
            //
            // 🚨 …but NOT YET, and that "yet" is the whole of issue #1360. This re-ask used to
            // fire SYNCHRONOUSLY out of the NACK callback, justified by "by the time that verdict
            // is produced the hub is already at RunLevel Dead — so the re-ask lands on a FRESH
            // activation". It is not: MessageService NACKs from `RunLevel >= DisposeHostedHubs`,
            // a phase in which the dying hub is STILL registered in its parent's
            // HostedHubsCollection (it removes itself in the later ShutDown phase, from
            // DisposeImpl's `disposables`). So routing resolved every re-ask to the SAME dying
            // instance, which NACKed it identically and immediately: the whole bounded budget
            // burned inside one teardown window — measured at FOUR rejections in 11 ms on
            // MeshWeaver.Plugins run 31645120599 — and the stream was orphaned for good.
            //
            // A WARM handle hides that (its replayed snapshot satisfies readers, and the owner's
            // next write re-converges it through the change-feed latch, ~2.0 s). A COLD handle has
            // nothing to replay, so the orphaning is terminal: MeshNodeStreamHandle.UpdateRemote's
            // initial-state wait runs out its full 30 s and aborts with "no initial state arrived
            // for '…' within 30s" — the abort that failed #1360's install with 0 nodes written.
            // Pinned by ColdHandleRecycleStarvationTest.
            //
            // The cure is to spend each attempt on a state that can actually answer: gate it on
            // the rejecting instance's own DisposalCompleted. This is NOT a retry, a backoff or a
            // watchdog — nothing polls, no timer runs, no bound moved, and the budget is the same
            // MaxRecycleReArms. It is the same event-driven re-ask, fired once the event it was
            // always waiting for has actually happened. PackageInstaller.RootTeardownSettled
            // already holds itself to exactly this contract one layer up; the sync stream
            // underneath it did not.
            //
            // It reuses Resubscribe, so it inherits the in-flight guard, ExpectResubscribeFull
            // (the mirror must accept a re-snapshot stamped below its cached frame version) and
            // the System impersonation. The re-ask asks for the CURRENT snapshot — it never
            // re-asserts a cached frame, which is the #945 shape and the opposite of what this
            // does. Bounded so that an owner stuck in a recycle LOOP degrades to the pre-existing
            // "orphaned" behaviour instead of trading a stalled stream for a storm.
            // Concat, not SelectMany: attempt N+1's wait starts only after attempt N's has ended,
            // so two rejections arriving together can never sit on two teardowns at once and race
            // into Resubscribe's in-flight guard. One at a time, without a lock.
            var recycleReArms = 0;
            var recycleReArm = rejectedByRecycle
                .Where(_ => Interlocked.Increment(ref recycleReArms) <= MaxRecycleReArms)
                .Select(reason => OwnerTeardownSettled().Select(_ => reason))
                .Concat()
                .Subscribe(
                    reason => Resubscribe(reason),
                    ex => logger.LogDebug(ex,
                        "Stream {StreamId}: recycle re-arm stream faulted", reduced.StreamId));
            reduced.RegisterForDisposal(recycleReArm);

            // Completes once the teardown that just rejected us has finished, so the re-ask lands
            // on a fresh activation instead of racing the same dying instance. Event-driven: it
            // observes that hub instance's DisposalCompleted, a ReplaySubject(1), so a disposal
            // that finishes between the probe and the subscribe still answers immediately. When
            // there is nothing to wait for — no local instance (the owner lives on another silo,
            // or its teardown already completed and it left the collection), or one that is not
            // disposing — the re-ask proceeds at once, exactly as before.
            //
            // No .Timeout here, deliberately: this join must not out-run the answer it joins
            // (#1317). MessageHub.Dispose arms a disposal watchdog that force-tears-down and
            // signals DisposalCompleted as its last act, so the leg is already guaranteed
            // terminal; the caller's own budget (UpdateRemote's initial-state wait) is the
            // deadline that belongs to the caller.
            IObservable<Unit> OwnerTeardownSettled()
            {
                var live = LocalInstanceOf(owner);
                if (live is null || !live.IsDisposing)
                    return Observable.Return(Unit.Default);
                logger.LogDebug(
                    "Stream {StreamId}: owner {Owner} is mid-teardown (RunLevel={RunLevel}) — "
                    + "holding the re-ask until its disposal completes",
                    reduced.StreamId, owner, live.RunLevel);
                return live.DisposalCompleted
                    .Take(1)
                    .Catch<Unit, Exception>(_ => Observable.Return(Unit.Default))
                    .DefaultIfEmpty(Unit.Default);
            }

            // The local hub instance serving `address`, or null when none does. A pure probe —
            // HostedHubCreation.Never is a dictionary lookup that must never activate a hub as a
            // side effect.
            //
            // 🚨 It walks UP, because the subscriber is almost never the owner's host: a per-node
            // hub is hosted by the MESH hub, while `hub` here is whoever opened the stream — a
            // `cache/{meshId}` or client hub, i.e. a SIBLING under that same mesh. And it cannot
            // shortcut through DI: every hub registers ITSELF as `IMessageHub` in its own service
            // collection (MessageHubConfiguration.ConfigureServices), so
            // `hub.ServiceProvider.GetService<IMessageHub>()` hands back `hub`. The parent link is
            // `Configuration.ParentHub`, which resolves `IMessageHub` from the PARENT provider.
            // Depth-capped: the chain is finite by construction (a root hub has no parent), and
            // the cap only ensures a malformed tree degrades to today's behaviour rather than
            // spinning. Failures answer `null`, which likewise means "re-ask now", exactly as
            // before this gate existed.
            IMessageHub? LocalInstanceOf(Address address)
            {
                try
                {
                    var h = hub;
                    for (var depth = 0; h is not null && depth < MaxHostLookupDepth; depth++)
                    {
                        if (h.GetHostedHub(address, HostedHubCreation.Never) is { } found)
                            return found;
                        h = h.Configuration.ParentHub;
                    }
                }
                catch (Exception ex)
                {
                    // ParentHub re-resolves from ParentServiceProvider, which can already be
                    // disposed when a whole tree is going down. Probing must never throw into
                    // the NACK path.
                    logger.LogDebug(ex,
                        "Stream {StreamId}: could not probe the local instance of {Owner}",
                        reduced.StreamId, address);
                }

                return null;
            }

            // 🚨 THE LOST INITIAL — and why the first heartbeat comes EARLY.
            //
            // Measured on memex 2026-07-27 from the compile activity log's microsecond timestamps:
            // source discovery took 45.20s on a healthy mesh and 90.19s (two misses) during the
            // outage, against 2.6s of actual Roslyn work. 45.20s is one HeartbeatInterval to within
            // 200ms. Types then crossed the 60s settle window and every plugin root served the
            // "did not settle" fallback; the same silence keeps an instance's NodeType stream from
            // ever seeing a fresh build, which is why the overlay self-heal had nothing to fire on
            // and only a process restart cleared it. One bug, three symptoms.
            //
            // The tell is WHERE the data finally arrives: exactly on a heartbeat. The owner is not
            // slow — it is holding the delivery until something pokes it, and the heartbeat is the
            // poke. Every other recovery path here is event-driven (change feed → latch), and a
            // stream whose owner ACKED the SubscribeRequest and then went quiet produces no event
            // at all, so nothing runs until the first HeartBeatEvent 45s later.
            //
            // So POKE SOONER. Not a re-subscribe — that creates a sync/{ClientId} hub on the
            // owner's single-threaded action block per attempt, which is the storm
            // ChangeFeedResubscribeCoalesceTest exists to prevent (and an earlier version of this
            // fix did exactly that, firing on healthy streams under CI load). Just start the
            // EXISTING heartbeat early: same fire-and-forget [SystemMessage] this stream already
            // sends every 45s forever, merely with its first tick at FirstHeartbeat instead of a
            // full interval. One extra cheap message per stream, no new subscription, no new
            // protocol, and it targets the mechanism that demonstrably delivers the data.
            var syncOptions = hub.ServiceProvider
                .GetService<Microsoft.Extensions.Options.IOptions<SyncStreamOptions>>()?.Value;
            var heartbeatInterval = syncOptions?.HeartbeatInterval ?? TimeSpan.FromSeconds(45);
            var firstHeartbeat = syncOptions?.FirstHeartbeat ?? FirstHeartbeat;
            // 🚨 The heartbeat must NOT strongly pin `hub`. Observable.Interval's timer lives
            // on the process-global Rx TimerQueue (a GC root); capturing `hub` in the tick
            // closure keeps an ABANDONED hub alive forever — e.g. a RunLevel=1 partial
            // activation that never reaches teardown, so `keepAlive` is never disposed. That is
            // the recurring MeshHub_IsCollected leak whose GC chain reads
            // ROOT→TimerQueue→ConcurrencyAbstractionLayerImpl+PeriodicTimer→hub. Hold the hub
            // WEAKLY (same pattern as MessageHub.InstallStaleCallbackScanner): a live, in-use
            // hub stays reachable via the mesh/cache so the heartbeat keeps firing normally;
            // once the hub is unreferenced the next tick self-disposes the timer and releases it.
            var weakHub = new WeakReference<IMessageHub>(hub);
            var sub = new System.Reactive.Disposables.SingleAssignmentDisposable();
            // NEVER later than the configured cadence: a caller that asks for a 200ms heartbeat
            // (HeartbeatFireAndForgetTest) must still get its first tick at 200ms. The early tick is
            // a floor-lowering for LONG intervals, not a delay imposed on short ones — taking the
            // sooner of the two keeps every existing cadence exactly as it was.
            var firstTick = heartbeatInterval < firstHeartbeat ? heartbeatInterval : firstHeartbeat;
            sub.Disposable = Observable.Timer(firstTick, heartbeatInterval)
                .Subscribe(_ =>
                {
                    if (!weakHub.TryGetTarget(out var h)
                        || h.RunLevel > MessageHubRunLevel.Started)
                    {
                        // Hub collected, or past Started (Quiescing/Disposed/abandoned) — stop
                        // ticking so the TimerQueue no longer roots anything through this closure.
                        sub.Dispose();
                        return;
                    }
                    // HeartBeatEvent is [SystemMessage] — PostPipeline accepts null AccessContext
                    // without warning. No identity stamp needed; receiver doesn't gate on principal.
                    //
                    // 🚨 FIRE-AND-FORGET — do NOT observe the delivery. A HEALTHY owner never acks a
                    // HeartBeatEvent, so OBSERVING it registered a hub callback that never resolved: the
                    // observe's Rx .Timeout completed the OBSERVABLE but left the underlying callback
                    // pending on THIS (cache) hub. Across many live sync streams those leaked
                    // HeartBeatEvent callbacks piled up (hundreds pending >30 s — the [STALE-CALLBACK]
                    // scan) until the hub's action block/liveness probe stalled: the doc-crawl / prod
                    // cache-hub wedge. The heartbeat has no ack to consume and needs none — its only job
                    // is to keep the owner grain alive, which the Post itself does. An undeliverable
                    // heartbeat is [CanBeIgnored], so routing DROPS it without a NACK (RoutingServiceBase
                    // AND RoutingGrain.PostFailureToSender both skip [CanBeIgnored]) — no NotFound storm,
                    // nothing to observe. A recycled/restarted owner is re-detected by the change-feed
                    // resubscribe below — the sole recycled-grain detector now that the heartbeat is
                    // fire-and-forget. A permanently-gone owner (a one-shot import-activity lock whose
                    // change feed fires no pulse) is simply heart-beaten into routing's ignore path every
                    // interval — a benign dropped post, not a storm — until this subscriber hub is itself
                    // collected (the weak-ref check above disposes the timer then).
                    h.Post(new HeartBeatEvent(), o => o.WithTarget(owner));
                });
            // On keepAlive (not directly on reduced): a terminal NotFound from the initial
            // SubscribeRequest disposes keepAlive → stops this heartbeat. Normal teardown
            // still reaches it because keepAlive is itself registered on reduced's disposal.
            keepAlive.Add(sub);

            // Resubscribe when the mesh change feed reports a Created/Deleted event
            // on the owner's path. This is the sole recycled-grain detector now that
            // heartbeats are fire-and-forget. Compare against Address.Path (segments
            // only) — ToString() can include a "~host" suffix for hosted addresses
            // which never matches MeshChangeEvent.Path (the bare node.Path).
            //
            // 🚨 COALESCE the change-feed-triggered resubscribe. The mesh change feed fires
            // one event per owner write; high-frequency owner writes (e.g. a per-HTTP-request
            // `_UserActivity` update) produce a BURST of events. A SINGLE resubscribe already
            // fetches the LATEST owner snapshot regardless of how many writes fired, so a
            // per-event resubscribe is redundant AND harmful: each resubscribe posts a fresh
            // SubscribeRequest whose handling on the owner SYNCHRONOUSLY creates a
            // `sync/{id}` hub on the owner's single-threaded action block (SynchronizationStream
            // ctor → Host.GetHostedHub). A burst of those serial creations starves the owner's
            // action block so it cannot ack OTHER subscribers' SubscribeRequests within the
            // callback timeout — the cache-hub wedge. The in-flight guard inside Resubscribe
            // only collapses events that arrive WHILE a resubscribe is mid-flight; the moment
            // the owner acks one, the guard clears and the next settled event fires another
            // resubscribe — so a stream of owner writes produces one resubscribe PER write.
            // Push each change-feed pulse through a Subject and Throttle it: a burst within the
            // window collapses to a SINGLE resubscribe (Throttle emits the last item only after
            // a quiet period). Recreate detection is preserved — a genuine owner restart still
            // triggers a resubscribe, just debounced by the window. Reactive only: no timer
            // watchdog, no async/await.
            var streamOptions = hub.ServiceProvider
                .GetService<Microsoft.Extensions.Options.IOptions<SyncStreamOptions>>()?.Value;
            var resubscribeWindow = streamOptions?.ChangeFeedResubscribeWindow ?? TimeSpan.FromSeconds(1);
            var stalenessGrace = streamOptions?.ChangeFeedStalenessGrace ?? TimeSpan.FromSeconds(1);
            var changeFeedPulses = new System.Reactive.Subjects.Subject<System.Reactive.Unit>();

            // 🚨 VERSION-GATED resubscribe for MeshNode streams. The change feed fires one event
            // per owner write; a HEALTHY subscriber receives that same write through its own
            // subscription, so resubscribing on it is pure churn — at scale it is the storm that
            // starved prod's hubs (one hot ApiToken node written per request reached version
            // 8939 in a day with 85 subscriber streams, each resubscribing on every write).
            // But the event is also the SOLE recycled-grain detector: a subscriber orphaned by a
            // disposed owner grain receives NOTHING, and the post-recycle write's feed event is
            // its only signal (pinned by ResubscribeOnOwnerDisposeTest — grain disposal emits no
            // node-lifecycle event, so an event-KIND filter breaks recovery).
            // The precise discriminator is the VERSION the event announces: track the highest
            // node version received through the stream; on the coalesced pulse wait a short
            // grace, then resubscribe ONLY when the stream is still BEHIND the announced version.
            // Healthy subscribers catch up through their own emissions and skip; orphaned ones
            // stay behind and refresh. Streams whose payload carries no version (non-MeshNode
            // reductions announce version 0) keep today's always-resubscribe behavior.
            var announcedVersion = 0L;
            var receivedVersion = 0L;
            // TReduced may carry a monotonic node Version (MeshNode does); resolved by NAME
            // because Data sits below Mesh.Contract in the project graph and cannot reference
            // MeshNode (same reason the change feed itself is reflection-resolved above).
            // Absent → receivedVersion stays 0 → the gate stays open → today's behavior.
            var reducedVersionProperty = typeof(TReduced).GetProperty("Version", typeof(long));
            keepAlive.Add(reduced.Subscribe(
                ci =>
                {
                    object? value = ci is null ? null : ci.Value;
                    if (value is not null && reducedVersionProperty?.GetValue(value) is long v)
                        InterlockedMax(ref receivedVersion, v);
                },
                // Passive tracker: the stream's fault (e.g. owner NotFound) is surfaced by the
                // stream's real subscribers; an observer without onError would RETHROW on the
                // emission thread and derail that propagation.
                _ => { }));

            keepAlive.Add(
                changeFeedPulses
                    // Throttle (debounce) collapses a burst to one trailing emission…
                    .Throttle(resubscribeWindow)
                    // …then give the stream's own emission the grace window to catch up…
                    .SelectMany(_ => Observable.Timer(stalenessGrace))
                    // …and only refresh when the stream is still behind what the feed announced.
                    .Where(_ => System.Threading.Interlocked.Read(ref receivedVersion)
                        < System.Threading.Interlocked.Read(ref announcedVersion))
                    .Subscribe(
                        _ => Resubscribe(
                            $"change feed announced v{Interlocked.Read(ref announcedVersion)} but stream is at v{Interlocked.Read(ref receivedVersion)} (stale/recycled owner)"),
                        ex => logger.LogWarning(ex,
                            "Stream {StreamId}: change-feed coalescing stream errored.",
                            reduced.StreamId)));
            keepAlive.Add(changeFeedPulses);

            var ownerPath = owner.Path;
            var changeFeedSub = TrySubscribeOwnerPathChangeFeed(
                hub.ServiceProvider, logger, ownerPath,
                version =>
                {
                    // A version-less event (0) must still trigger the resubscribe path for
                    // version-less streams: announce MAX(version, received+1) so the Where
                    // gate stays open unless the stream demonstrably caught up.
                    InterlockedMax(ref announcedVersion,
                        version > 0 ? version : Interlocked.Read(ref receivedVersion) + 1);
                    changeFeedPulses.OnNext(System.Reactive.Unit.Default);
                });
            if (changeFeedSub != null)
                keepAlive.Add(changeFeedSub); // torn down with the heartbeat on terminal NotFound
        }

        return reduced;
    }

    /// <summary>
    /// The frame a resubscribing subscriber is served: the stream's CURRENT state re-asserted as a
    /// <see cref="ChangeType.Full"/>, stamped with the version that state already carries.
    /// <para>🚨 MUST be invoked from INSIDE the stream's update turn (i.e. from the
    /// <c>Update</c> lambda) — that is what makes <c>stream.Current</c> the freshest committed
    /// state rather than a snapshot bound before the turn was scheduled. Re-asserting a
    /// pre-turn capture rolls the stream BACKWARD over frames that committed in between, and
    /// those entries are never re-sent (issue #945). And the version MUST come off the state
    /// itself — the owner's content clock — never <c>stream.Hub.Version</c>, which is this
    /// per-subscriber sync hub's unrelated message counter; mixing clocks on one stream either
    /// sneaks a stale Full past the receive-side monotonicity guard or freezes the mirror by
    /// making every later content patch look stale. Same rule as
    /// <c>StandardReducers.PatchJsonElement</c>.</para>
    /// <returns><c>null</c> when there is nothing to assert yet (the initial subscribe is still
    /// hydrating) — a no-op update.</returns>
    /// </summary>
    internal static ChangeItem<TReduced>? BuildReassertFrame<TReduced>(
        ISynchronizationStream<TReduced> stream)
        => stream.Current is { Value: not null } live
            ? new ChangeItem<TReduced>(live.Value, stream.StreamId, live.Version)
            : null;

    internal static ISynchronizationStream CreateSynchronizationStream<TReduced, TReference>(
        this IWorkspace workspace,
        IMessageDelivery<SubscribeRequest> delivery
)
    where TReference : WorkspaceReference
    {
        var hub = workspace.Hub;
        var logger = GetLogger(hub.ServiceProvider);
        var request = delivery.Message with { Subscriber = delivery.Sender };

        // 🚨 RE-SUBSCRIBE IS A REFRESH, NOT A SECOND SUBSCRIPTION (issue #606).
        //
        // Resubscribe() re-posts a SubscribeRequest carrying the SAME StreamId — it means
        // "re-assert my stream", not "open another one". Building a second reduced stream here
        // left the FIRST one live and rendering forever (nothing but an UnsubscribeRequest ever
        // disposes a server-side stream, and a resubscribe never sends one). For a
        // LayoutAreaReference each of those is a whole LayoutAreaHost + render graph, so every
        // change-feed pulse on the owner path permanently added one more render pipeline —
        // the unbounded growth that killed the pod.
        //
        // Serve the refresh off the stream we ALREADY have: re-assert its current state as a
        // Full, which the outbound forwarding subscription below (registered when that stream
        // was created) delivers to the subscriber — exactly the "fresh snapshot" the caller
        // asked for, and exactly what its ExpectResubscribeFull latch is armed to accept.
        // A genuinely orphaned mirror (owner recycled ⇒ new workspace ⇒ empty registry) still
        // takes the full create path below.
        if (workspace is Workspace registry
            && registry.GetClientSubscription(request.Subscriber, request.StreamId, request.Reference)
                is ISynchronizationStream<TReduced> alreadyServing)
        {
            logger.LogDebug(
                "Owner {Owner} already serves stream {StreamId} for {Subscriber} — re-asserting the current snapshot instead of creating a second stream",
                hub.Address, request.StreamId, request.Subscriber);
            // 🚨 Read the snapshot IN-TURN and carry ITS version — never a value captured out
            // here, never this stream's own hub clock. Both were data loss (issue #945):
            //
            //   • STALE CAPTURE. `Update` posts an UpdateStreamRequest; the lambda runs LATER on
            //     the stream's action block. A snapshot bound out here and written back blindly
            //     (`_ => snapshot`) OVERWRITES every frame that committed in between — the stream
            //     moves BACKWARD, and because the owner-side outbound JSON cursor is re-based on
            //     that rolled-back state, the erased entries are never re-sent. Subsequent patches
            //     keep applying cleanly on top, so the mirror tracks the owner forever at a
            //     CONSTANT deficit with no error anywhere. Measured under a 288-write burst:
            //     mirror at 245 entries/v246 → 234 entries/v235, ending 11 entries short of the
            //     owner while every write acked success.
            //   • WRONG CLOCK. Content frames on this stream carry the OWNER's data-source clock
            //     (WorkspaceExtensions.ApplyChanges → primary stream Hub.Version, carried through
            //     the reduce as ChangeItem.Version). Stamping the per-subscriber SYNC hub's clock
            //     here mixes two unrelated counters on one stream — the same defect StandardReducers
            //     documents ("CARRY the base version … NEVER stream.Hub.Version"). It is what lets
            //     a stale Full slip past the receive-side monotonicity guard that exists to drop
            //     exactly that; when the sync clock instead runs AHEAD, every later content patch
            //     is dropped as "stale" and the mirror freezes.
            //
            // Re-asserting the CURRENT state at ITS OWN version is still a full authoritative
            // snapshot — the orphaned mirror re-converges (a Full always lands, and the mirror
            // that resubscribed is by definition not ahead) — but it can neither erase newer
            // state nor poison the mirror's version.
            if (alreadyServing.Current is { Value: not null })
                alreadyServing.Update(
                    _ => BuildReassertFrame(alreadyServing),
                    ex => logger.LogWarning(ex,
                        "Stream {StreamId}: could not re-assert snapshot for resubscribing subscriber {Subscriber}",
                        alreadyServing.StreamId, request.Subscriber));
            // Nothing yet to re-assert (the initial subscribe is still hydrating) — its own
            // first Full is already on its way to this subscriber; do NOT build a second stream.
            return alreadyServing;
        }

        var fromWorkspace = workspace
            .ReduceManager
            .ReduceStream<TReduced>(
                workspace,
                request.Reference, config => config.WithClientId(request.StreamId).WithSubscriber(request.Subscriber)
            );

        var reduced =
            fromWorkspace as ISynchronizationStream<TReduced>
            ?? throw new DataSourceConfigurationException(
                $"No reducer defined for {typeof(TReference).Name} from  {typeof(TReference).Name}"
            );


        // Use single synchronized subscription for both initial data and ongoing changes.
        // SubscribeAck is sent by HandleSubscribeRequest to close the hub.Observe(subscribeRequest)
        // pending callback immediately — DataChangedEvents always use WithTarget so they
        // flow via RouteStreamMessage to the inner sync hub regardless of callback state.
        reduced.RegisterForDisposal(
            reduced
                // 🚨 ALWAYS forward a FULL to the subscriber — a Full is the owner's complete
                // authoritative snapshot (the initial subscribe state, or a re-assert/rollback),
                // NEVER the subscriber's own echo (subscribers only ever send Patches via
                // DataChangeRequest). The bare `!ClientId.Equals(ChangedBy)` echo-filter dropped
                // the INITIAL Full whenever ChangedBy and the subscriber's ClientId collided —
                // e.g. both empty (no StreamId on the SubscribeRequest) — leaving the subscriber
                // dark until the next 45s heartbeat re-emitted. Only PATCHES are echo-suppressed.
                // (The client side already applies Fulls unconditionally — UpdateStream's
                // monotonicity guard is PATCHES-ONLY; this mirrors that contract on the owner.)
                .ToDataChanged<TReduced, DataChangedEvent>(
                    c => c.ChangeType == ChangeType.Full || !reduced.ClientId.Equals(c.ChangedBy))
                .Synchronize()
                .Where(x => x is not null)
                .Select(x => x!)
                .Subscribe(e =>
                {
                    logger.LogDebug("Owner {owner} sending data to subscriber {subscriber}", reduced.Owner, request.Subscriber);
                    // Attribute the fan-out to the SUBSCRIBER's subscribe-time identity, carried on
                    // the SubscribeRequest delivery. The mesh-node cache hydrates every per-path
                    // stream under MeshNodeCacheIdentity (Read-only) — so the owner's outbound
                    // DataChangedEvent now carries that real principal instead of an empty
                    // AccessContext that the PostPipeline would fail closed on and warn about
                    // ("posted with no AccessContext"). A Blazor-client subscriber rides its own
                    // user context the same way. A genuinely context-less subscribe still posts
                    // unstamped and still warns — that correctly flags a missing identity rather
                    // than inventing one (the deleted 2026-05-21 "stamp hub-self" prod bug).
                    hub.Post(e, o =>
                    {
                        var opt = o.WithTarget(request.Subscriber);
                        return delivery.AccessContext is not null
                            ? opt.WithAccessContext(delivery.AccessContext)
                            : opt;
                    });
                },
                ex =>
                {
                    logger.LogWarning(ex, "Workspace stream error for subscriber {Subscriber}, propagating StreamErrorEvent", request.Subscriber);
                    // StreamErrorEvent (a StreamMessage) is routed via the subscriber
                    // hub's RouteStreamMessage to the per-stream sub-hub, which
                    // OnErrors the local SynchronizationStream. A plain
                    // DeliveryFailure stops at the parent hub because it isn't a
                    // StreamMessage — the subscriber would stay live forever.
                    hub.Post(new StreamErrorEvent(request.StreamId, ex.Message),
                        o => o.WithTarget(request.Subscriber));
                })
        );

        // NOTE: The following subscription was causing an infinite feedback loop.
        // When a client sends a DataChangeRequest, the workspace processes it and updates the stream.
        // The stream emits with ChangedBy = ClientId, matching the predicate below, which calls
        // RequestChange() again, creating an infinite loop.
        // All changes should flow through DataChangeRequest messages, not through stream subscriptions.
        // Removed to fix the feedback loop bug.

        // // outgoing data changed
        // reduced.RegisterForDisposal(
        //     reduced
        //         .ToDataChangeRequest(c => reduced.ClientId.Equals(c.ChangedBy))
        //         .Synchronize()
        //         .Subscribe(e =>
        //         {
        //             logger.LogDebug("Issuing change request from stream {subscriber} to owner {owner}", reduced.StreamId, reduced.Owner);
        //             reduced.Host.GetWorkspace().RequestChange(e);
        //         })
        // );

        // Record it as THE stream for this (subscriber, StreamId) so a later resubscribe
        // refreshes it instead of stacking another one (see the guard at the top). Registered
        // AFTER the outbound forwarding subscription exists, so a re-assert can never find a
        // stream that has no way to reach the subscriber. Unregisters itself on disposal.
        (workspace as Workspace)?.RegisterClientSubscription(
            request.Subscriber, request.StreamId, request.Reference, reduced);

        return reduced;
    }
    private static IObservable<TChange?> ToDataChanged<TReduced, TChange>(
        this ISynchronizationStream<TReduced> stream, Func<ChangeItem<TReduced>, bool> predicate) where TChange : JsonChange
    {
        // Loss-detection chain (issue #1081): every emitted frame carries the Version of the frame
        // emitted immediately BEFORE it on this same forwarding subscription (-1 for the first).
        // The receive side (SynchronizationStream.UpdateStream) compares a Patch's BasedOnVersion
        // against the version it last applied: a mismatch proves a frame was lost in transport
        // (at-most-once memory streams) and triggers a fresh-snapshot resync instead of tracking
        // the owner forever at a silent deficit. Serial by construction — the Synchronize() below
        // makes Select single-threaded, so the closure variable needs no interlocking. Frames that
        // are SKIPPED here (value-equal, no updates) never enter the chain, so legitimate version
        // gaps from skipped frames do not false-trigger a resync.
        var lastSentVersion = -1L;
        return stream
            .Synchronize()
            .Where(predicate)
            .Select(x =>
            {
                var logger = GetLogger(stream.Hub.ServiceProvider);
                logger.LogDebug("ToDataChanged processing change item: StreamId={StreamId}, ChangeType={ChangeType}, ChangedBy={ChangedBy}, UpdatesCount={UpdatesCount}",
                    stream.ClientId, x.ChangeType, x.ChangedBy, x.Updates.Count);

                var currentJson = stream.Get<JsonElement?>();
                if (currentJson is null || x.ChangeType == ChangeType.Full)
                {
                    logger.LogDebug("Processing full change for stream {StreamId}, currentJson is null: {IsNull}", stream.ClientId, currentJson is null);
                    var previousJson = currentJson;
                    currentJson = JsonSerializer.SerializeToElement(x.Value, x.Value?.GetType() ?? typeof(object), stream.Host.JsonSerializerOptions);
                    // 🚨 A FULL data push ALWAYS goes out — it re-asserts the owner's
                    // complete authoritative state (SetFull overwrite, rollback / resync)
                    // and a subscriber that diverged optimistically only re-converges if
                    // the Full reaches it. NEVER suppress a value-equal Full. (Re-entering
                    // this branch with a populated cache requires ChangeType.Full, so the
                    // equality short-circuit only ever fired for value-equal Fulls — this
                    // restores the documented "a Full lands unconditionally" contract.)
                    if (x.ChangeType != ChangeType.Full && Equals(previousJson, currentJson))
                    {
                        logger.LogDebug("Previous JSON equals current JSON for stream {StreamId}, returning null", stream.ClientId);
                        return null;
                    }
                    stream.Set(currentJson);
                    logger.LogDebug("Generated full DataChangedEvent for stream {StreamId}", stream.ClientId);
                    var fullEvent = (TChange?)Activator.CreateInstance(
                        typeof(TChange),
                        stream.ClientId,
                        x.Version,
                        new RawJson(currentJson.ToString() ?? string.Empty),
                        ChangeType.Full,
                        x.ChangedBy ?? string.Empty,
                        lastSentVersion);
                    lastSentVersion = x.Version;
                    return fullEvent;
                }
                else
                {
                    if (x.Updates.Count == 0)
                    {
                        logger.LogWarning("No updates in change item for stream {StreamId}, skipping DataChangedEvent generation. ChangeType: {ChangeType}, ChangedBy: {ChangedBy}",
                            stream.ClientId, x.ChangeType, x.ChangedBy);
                        return null;
                    }
                    var patch = x.Updates.ToJsonPatch(stream.Host.JsonSerializerOptions, stream.Reference as WorkspaceReference);
                    var patchJson = JsonSerializer.Serialize(patch, stream.Host.JsonSerializerOptions);
                    try
                    {
                        // Apply patch with correct RFC 6901 unescaping
                        // The json-everything library doesn't properly unescape ~1 -> / in property names
                        (currentJson, _) = ApplyPatchWithCorrectUnescaping(patchJson, currentJson.Value, stream.Host.JsonSerializerOptions);
                    }
                    catch (StaleStreamStateException stale)
                    {
                        // The cached JSON drifted from the authoritative entity store
                        // (concurrent updates whose Updates were computed against an older
                        // snapshot). Regenerate from the current value and emit a Full
                        // so every subscriber resyncs cleanly.
                        logger.LogWarning(stale,
                            "Stale JSON snapshot for stream {StreamId}; regenerating Full from current value.",
                            stream.ClientId);
                        currentJson = JsonSerializer.SerializeToElement(
                            x.Value, x.Value?.GetType() ?? typeof(object),
                            stream.Host.JsonSerializerOptions);
                        stream.Set(currentJson);
                        var resyncFull = (TChange?)Activator.CreateInstance(
                            typeof(TChange),
                            stream.ClientId,
                            x.Version,
                            new RawJson(currentJson.Value.ToString() ?? string.Empty),
                            ChangeType.Full,
                            x.ChangedBy ?? string.Empty,
                            lastSentVersion);
                        lastSentVersion = x.Version;
                        return resyncFull;
                    }
                    stream.Set(currentJson);
                    var patchEvent = (TChange?)Activator.CreateInstance
                    (
                        typeof(TChange),
                        stream.ClientId,
                        x.Version,
                        new RawJson(patchJson),
                        x.ChangeType,
                        x.ChangedBy ?? string.Empty,
                        lastSentVersion
                    );
                    lastSentVersion = x.Version;
                    return patchEvent;
                }


            });
    }





    /// <summary>
    /// Applies the stream's registered patch function to produce a typed change from a JSON patch.
    /// </summary>
    /// <typeparam name="TReduced">The reduced stream's state type.</typeparam>
    /// <param name="stream">The synchronization stream whose reduce manager supplies the patch function.</param>
    /// <param name="currentState">The current typed state of the stream.</param>
    /// <param name="currentJson">The current state serialized as JSON.</param>
    /// <param name="patch">The JSON patch to apply, or null for a full update.</param>
    /// <param name="changedBy">Identifier of the principal that made the change.</param>
    /// <returns>The resulting change item, or null when no patch function is registered.</returns>
    public static ChangeItem<TReduced>? ToChangeItem<TReduced>(
        this ISynchronizationStream<TReduced> stream,
        TReduced currentState,
        JsonElement currentJson,
        JsonPatch? patch,
        string changedBy)
    {
        return stream.ReduceManager.PatchFunction?.Invoke(stream, currentState, currentJson, patch, changedBy);
    }


    /// <summary>
    /// Converts a JSON patch over a whole entity-store projection into per-entity
    /// <see cref="EntityUpdate"/>s, normalizing collection names through the optional type registry and
    /// capturing both the old and new value for each affected entity.
    /// </summary>
    /// <param name="current">The store projection before the patch (source of old values).</param>
    /// <param name="updated">The store projection after the patch (source of new values).</param>
    /// <param name="patch">The JSON patch describing the changes.</param>
    /// <param name="options">Serializer options used to decode pointer segments.</param>
    /// <param name="typeRegistry">Optional registry used to normalize raw collection names to their short form.</param>
    /// <returns>One distinct update per affected entity (deduplicated by id and collection).</returns>
    public static IReadOnlyCollection<EntityUpdate> ToEntityUpdates(
        this JsonElement current,
        JsonElement updated,
        JsonPatch patch,
        JsonSerializerOptions options,
        ITypeRegistry? typeRegistry = null)
        => patch.Operations.Select(p =>
            {
                var id = p.Path.SegmentCount == 0 ? null : p.Path.GetSegment(1).ToString();
                var rawCollection = p.Path.GetSegment(0).ToString();

                // Normalize collection name using TypeRegistry to ensure consistency
                // This fixes the bug where JsonPatch paths contain full type names 
                // but CollectionsReference expects short names from TypeRegistry
                var collection = typeRegistry?.TryGetType(rawCollection, out var typeDefinition) == true
                    ? typeDefinition!.CollectionName
                    : rawCollection;

                var pointer = id == null ? JsonPointer.Create(collection) : JsonPointer.Create(collection, id);
                return new EntityUpdate(
                        collection,
                        DecodePointerSegment(id, options)!,
                        pointer.Evaluate(updated)!
                    )
                { OldValue = pointer.Evaluate(current) };
            })
            .DistinctBy(x => new { Id = x.Id is JsonElement je ? je.GetRawText() : x.Id?.ToString(), x.Collection })
            .ToArray();

    internal static (JsonElement, JsonPatch) UpdateJsonElement<TChange>(this TChange request, JsonElement? currentJson, JsonSerializerOptions options) where TChange : JsonChange
    {
        if (request.ChangeType == ChangeType.Full)
        {
            return (JsonDocument.Parse(request.Change.Content).RootElement, null!);
        }

        if (currentJson is null)
            throw new InvalidOperationException("Current state is null, cannot patch.");

        // Apply patch operations manually with correct RFC 6901 unescaping
        // The json-everything library stores segments in escaped form and Apply uses
        // escaped property names, which is incorrect per RFC 6901
        return ApplyPatchWithCorrectUnescaping(request.Change.Content, currentJson.Value, options);
    }

    private static (JsonElement, JsonPatch) ApplyPatchWithCorrectUnescaping(string patchJson, JsonElement currentJson, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.Parse(patchJson);
        var currentNode = JsonSerializer.SerializeToNode(currentJson, options);
        var operations = new List<PatchOperation>();

        foreach (var opElement in doc.RootElement.EnumerateArray())
        {
            var op = opElement.GetProperty("op").GetString();
            var pathString = opElement.GetProperty("path").GetString()!;

            // Parse path segments with RFC 6901 unescaping
            var segments = ParsePathSegments(pathString);

            JsonNode? value = null;
            if (opElement.TryGetProperty("value", out var valueElement))
            {
                value = JsonSerializer.SerializeToNode(valueElement, options);
            }

            // Apply the operation manually with correct unescaping
            switch (op)
            {
                case "add":
                    ApplyAdd(currentNode!, segments, value);
                    break;
                case "replace":
                    ApplyReplace(currentNode!, segments, value);
                    break;
                case "remove":
                    ApplyRemove(currentNode!, segments);
                    break;
                default:
                    // move/copy/test are never produced by ToJsonPatch, so this branch only ever
                    // sees a patch authored elsewhere. Defer them to MeshWeaver.Json's applier,
                    // which implements RFC 6902 (including correct ~0/~1 decoding) directly.
                    var parsedPath = JsonPointer.Parse(pathString);
                    var operation = op switch
                    {
                        // 🚨 Move/Copy take (FROM, PATH) — not (path, from).
                        "move" when opElement.TryGetProperty("from", out var fromEl) =>
                            PatchOperation.Move(JsonPointer.Parse(fromEl.GetString()!), parsedPath),
                        "copy" when opElement.TryGetProperty("from", out var fromEl) =>
                            PatchOperation.Copy(JsonPointer.Parse(fromEl.GetString()!), parsedPath),
                        "test" => PatchOperation.Test(parsedPath, value),
                        _ => throw new InvalidOperationException($"Unknown patch operation: {op}")
                    };
                    operations.Add(operation);
                    break;
            }
        }

        // If there were any fallback operations, apply them
        if (operations.Count > 0)
        {
            var fallbackPatch = new JsonPatch(operations);
            var result = fallbackPatch.Apply(currentNode);
            if (!result.IsSuccess)
                throw new InvalidOperationException(
                    $"Patch operation {result.Operation} failed: {result.Error}");
            // 🚨 result.Result — NOT `result`. Serializing the PatchResult itself would put
            // {"Result":…,"Error":…,"IsSuccess":…} into the stream's state.
            return (JsonSerializer.SerializeToElement(result.Result, options), fallbackPatch);
        }

        // Serialize back to JsonElement
        var resultElement = JsonSerializer.SerializeToElement(currentNode, options);
        // Create a dummy patch for the return value (we don't use it on the receiving end)
        var dummyPatch = JsonSerializer.Deserialize<JsonPatch>(patchJson, options)!;
        return (resultElement, dummyPatch);
    }

    private static string[] ParsePathSegments(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/")
            return Array.Empty<string>();

        // Split on / (first char is always /)
        var parts = path[1..].Split('/');
        var segments = new string[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            // RFC 6901 unescape: ~1 -> / and ~0 -> ~ (order matters)
            segments[i] = parts[i].Replace("~1", "/").Replace("~0", "~");
        }
        return segments;
    }

    private static void ApplyAdd(JsonNode root, string[] segments, JsonNode? value)
    {
        if (segments.Length == 0)
            throw new InvalidOperationException("Cannot add at root path");

        var parent = EnsureParentPath(root, segments);
        var key = segments[^1];

        if (parent is JsonObject obj)
            obj[key] = value;
        else if (parent is JsonArray arr)
        {
            if (key == "-") arr.Add(value);
            else if (int.TryParse(key, out var index))
            {
                if (index < 0 || index > arr.Count)
                    throw new StaleStreamStateException(
                        $"Stale patch: add at index {index} but array has {arr.Count} elements.");
                arr.Insert(index, value);
            }
        }
    }

    private static void ApplyReplace(JsonNode root, string[] segments, JsonNode? value)
    {
        if (segments.Length == 0)
            throw new InvalidOperationException("Cannot replace at root path");

        var parent = EnsureParentPath(root, segments);
        var key = segments[^1];

        if (parent is JsonObject obj)
            obj[key] = value;
        else if (parent is JsonArray arr && int.TryParse(key, out var index))
        {
            if (index < 0 || index >= arr.Count)
                throw new StaleStreamStateException(
                    $"Stale patch: replace at index {index} but array has {arr.Count} elements.");
            arr[index] = value;
        }
    }

    private static void ApplyRemove(JsonNode root, string[] segments)
    {
        if (segments.Length == 0)
            throw new InvalidOperationException("Cannot remove at root path");

        var parent = NavigateToParent(root, segments);
        if (parent is JsonObject obj)
            obj.Remove(segments[^1]);
        else if (parent is JsonArray arr && int.TryParse(segments[^1], out var index))
        {
            if (index < 0 || index >= arr.Count)
                throw new StaleStreamStateException(
                    $"Stale patch: remove at index {index} but array has {arr.Count} elements.");
            arr.RemoveAt(index);
        }
    }

    /// <summary>
    /// Navigates to the parent, creating intermediate JsonObject nodes if a primitive
    /// is encountered (e.g., replacing a string with an object tree).
    /// </summary>
    private static JsonNode? EnsureParentPath(JsonNode root, string[] segments)
    {
        JsonNode? current = root;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            var segment = segments[i];
            if (current is JsonObject obj)
            {
                var next = obj[segment];
                if (next is null or JsonValue)
                {
                    // Replace primitive/null with an empty object so we can navigate deeper
                    next = new JsonObject();
                    obj[segment] = next;
                }
                current = next;
            }
            else if (current is JsonArray arr && int.TryParse(segment, out var index))
            {
                current = arr[index];
            }
            else
            {
                return null;
            }
        }
        return current;
    }

    private static JsonNode? NavigateToParent(JsonNode root, string[] segments)
    {
        JsonNode? current = root;
        // Navigate to parent (all segments except last)
        for (int i = 0; i < segments.Length - 1; i++)
        {
            var segment = segments[i];
            if (current is JsonObject obj)
            {
                current = obj[segment];
            }
            else if (current is JsonArray arr && int.TryParse(segment, out var index))
            {
                current = arr[index];
            }
            else
            {
                // Can't navigate through a primitive (string/number/null) —
                // the patch replaces a leaf with a deeper structure.
                // Return null so the caller can skip or handle gracefully.
                return null;
            }
        }
        return current;
    }
    /// <summary>
    /// Converts a JSON patch scoped to a single collection into per-entity <see cref="EntityUpdate"/>s,
    /// resolving each entity's old value from the supplied collection snapshot.
    /// </summary>
    /// <param name="current">The collection snapshot before the patch (source of old values).</param>
    /// <param name="reference">The reference identifying the collection being patched.</param>
    /// <param name="updated">The collection projection after the patch (source of new values).</param>
    /// <param name="patch">The JSON patch describing the changes.</param>
    /// <param name="options">Serializer options used to decode entity ids.</param>
    /// <returns>One distinct update per affected entity (deduplicated by id and collection).</returns>
    public static IReadOnlyCollection<EntityUpdate> ToEntityUpdates(
        this InstanceCollection current,
        CollectionReference reference,
        JsonElement updated,
        JsonPatch patch,
        JsonSerializerOptions options)
        => patch.Operations.Select(p =>
        {
            var id = p.Path.GetSegment(0);


            JsonPointer? pointer = id == string.Empty ? null : CreatePointerFromSegments(id.ToString());
            var idSegment = id == string.Empty ? null : JsonSerializer.Deserialize<object>(id.ToString(), options)!;
            return new EntityUpdate(
                reference.Name,
                idSegment,
                pointer?.Evaluate(updated) ?? updated
            )
            { OldValue = idSegment == null ? current.Instances : current.Instances.GetValueOrDefault(idSegment) };
        })
        .DistinctBy(x => new { Id = x.Id is JsonElement je ? je.GetRawText() : x.Id?.ToString(), x.Collection })
        .ToArray();

    internal static (InstanceCollection, JsonPatch) UpdateJsonElement(this DataChangedEvent request, InstanceCollection current, JsonSerializerOptions options)
    {
        if (request.ChangeType == ChangeType.Full)
        {
            return (JsonDocument.Parse(request.Change.Content).RootElement.Deserialize<InstanceCollection>()!, null!);
        }

        if (current is null)
            throw new InvalidOperationException("Current state is null, cannot patch.");
        // Apply patch with correct RFC 6901 unescaping — the json-everything library's
        // Apply(JsonNode) doesn't properly unescape ~1 in property names
        var currentJson = JsonSerializer.SerializeToElement(current, typeof(InstanceCollection), options);
        var (updatedJson, patch) = ApplyPatchWithCorrectUnescaping(request.Change.Content, currentJson, options);
        var updated = updatedJson.Deserialize<InstanceCollection>(options);
        return (updated!, patch);
    }

    /// <summary>
    /// Human-readable failure reason for a <see cref="DataChangeResponse"/> whose
    /// <see cref="ActivityLog.Status"/> is Failed. The change-application path logs the real
    /// error on a SUB-activity while the top-level status only rolls up — so this walks every
    /// (sub-)activity, joins their error messages AND appends each one's activity path
    /// (<c>{HubPath}/_activity/{Id}</c>) so the caller can open the persisted activity and
    /// inspect the full detail instead of seeing a bare "Unknown error".
    /// </summary>
    private static string DescribeFailure(ActivityLog? log)
    {
        if (log is null)
            return "Unknown error (no activity log)";
        var failures = log.SelfAndDescendants()
            .SelectMany(a => a.Messages
                .Where(m => m.LogLevel >= LogLevel.Error)
                .Select(m => string.IsNullOrEmpty(a.HubPath)
                    ? $"{m.Message} [activity {a.Id}]"
                    : $"{m.Message} [activity {a.HubPath}/_activity/{a.Id}]"))
            .ToList();
        return failures.Count > 0
            ? string.Join("; ", failures)
            : $"Failed with no error message (activity {log.Id}, status {log.Status})";
    }

    internal static IObservable<DataChangeRequest> ToDataChangeRequest<TStream>(
        this ISynchronizationStream<TStream> stream, Func<ChangeItem<TStream>, bool> predicate)
        => stream
            .Synchronize()
            .Where(predicate)
            .Select(x => x.Updates.ToDataChangeRequest(stream.ClientId, stream.Host));



    internal static DataChangeRequest ToDataChangeRequest(this IEnumerable<EntityUpdate> updates,
        string clientId, IMessageHub host)
    {
        var options = host.JsonSerializerOptions;
        var workspace = host.GetWorkspace();
        return updates
            .GroupBy(x => new { x.Collection, x.Id })
            .Aggregate(new DataChangeRequest() { ChangedBy = clientId }, (e, g) =>
            {
                var first = g.First().OldValue;
                var last = g.Last().Value;

                if (last == null && first == null)
                    return e;
                if (last == null)
                    return e.WithDeletions(first!);

                // 🚨 Minimal-bytes: when we have a base (OldValue), a string key, and a
                // matching type, ship a compact EntityDelta (the splice) instead of the
                // whole entity for big string-heavy content (markdown / html). Only take
                // the delta path when the PARTITION resolves the same way the owner will
                // (non-partitioned type, or a resolved partition) — otherwise the owner
                // would route the delta to the wrong stream and silently drop the update.
                // EntityDelta.TryCompute also gates on size + only when the delta is
                // smaller; any miss → fall through to the full whole-replace path.
                if (first is not null && g.Key.Id is string idStr && first.GetType() == last.GetType())
                {
                    var partitioned = workspace.DataContext.GetTypeSource(last.GetType()) as IPartitionedTypeSource;
                    var partition = partitioned?.GetPartition(last);
                    if ((partitioned is null || partition is not null)
                        && EntityDelta.TryCompute(g.Key.Collection, idStr, partition, first, last, options) is { } deltaUpdate)
                        return e.WithUpdates(deltaUpdate);
                }

                // Treat as update regardless of OldValue — OldValue may be null
                // when the change was deserialized from a remote stream (not serialized).
                return e.WithUpdates(last);
            });
    }

    internal static JsonPatch ToJsonPatch(this IEnumerable<EntityUpdate> updates,
        JsonSerializerOptions options,
        WorkspaceReference? streamReference)
    {
        return streamReference switch
        {
            CollectionReference collection => CreateCollectionPatch(collection, options, updates),
            WorkspaceReference<EntityStore> => CreateEntityStorePatch(options, updates),
            null => CreateEntityStorePatch(options, updates),
            // Single-object references (e.g. MeshNodeReference) — patch at root level
            _ => CreateSingleObjectPatch(options, updates)
        };
    }

    private static JsonPatch CreateCollectionPatch(
        CollectionReference collection,
        JsonSerializerOptions options,
        IEnumerable<EntityUpdate> updates)
    {
        var collectionName = collection.Name;
        return new JsonPatch(updates
            .Where(e => e.Collection == collectionName)
            .GroupBy(x => x.Id)
            .Aggregate(Enumerable.Empty<PatchOperation>(), (e, g) =>
            {
                var first = g.First().OldValue;
                var last = g.Last().Value;
                string[] pointerSegments = g.Key == null
                    ? []
                    :
                    [
                        JsonSerializer.Serialize(g.Key, options)
                    ];
                var parentPath = CreatePointerFromSegments(pointerSegments);
                if (last == null && first == null)
                    return e;
                if (first == null)
                    return e.Concat([PatchOperation.Add(parentPath, JsonSerializer.SerializeToNode(last, options))]);
                if (last == null)
                    return e.Concat([PatchOperation.Remove(parentPath)]);
                var patches = first.CreatePatch(last, options).Operations;
                patches = patches.Select(p =>
                {
                    var newPath = parentPath.Combine(p.Path);
                    return CreatePatchOperation(p, newPath);
                }).ToArray();
                return e.Concat(patches);
            }).ToArray());
    }

    private static JsonPatch CreateSingleObjectPatch(JsonSerializerOptions options, IEnumerable<EntityUpdate> updates)
    {
        // For single-object streams (e.g. MeshNodeReference), generate root-level patches
        // without collection/id path segments
        return new JsonPatch(updates
            .Aggregate(Enumerable.Empty<PatchOperation>(), (e, u) =>
            {
                var first = u.OldValue;
                var last = u.Value;
                if (last == null && first == null)
                    return e;
                if (first == null)
                    return e.Concat([PatchOperation.Add(JsonPointer.Empty, JsonSerializer.SerializeToNode(last, options))]);
                if (last == null)
                    return e.Concat([PatchOperation.Remove(JsonPointer.Empty)]);
                var patches = first.CreatePatch(last, options).Operations;
                return e.Concat(patches);
            }).ToArray());
    }

    private static JsonPointer CreatePointerFromSegments(params string[] pointerSegments)
    {
        // Manually build RFC 6901 pointer with proper escaping:
        // ~ → ~0, / → ~1 within each segment
        var escaped = string.Concat(pointerSegments.Select(s =>
            "/" + s.Replace("~", "~0").Replace("/", "~1")));
        return JsonPointer.Parse(escaped);
    }

    private static JsonPatch CreateEntityStorePatch(JsonSerializerOptions options, IEnumerable<EntityUpdate> updates)
    {
        return new JsonPatch(updates
            .GroupBy(x => new { x.Collection, x.Id })
            .Aggregate(Enumerable.Empty<PatchOperation>(), (e, g) =>
            {
                var first = g.First().OldValue;
                var last = g.Last().Value;

                string[] pointerSegments = g.Key.Id == null
                    ? [g.Key.Collection]
                    :
                    [
                        g.Key.Collection,
                        JsonSerializer.Serialize(g.Key.Id, options)
                    ];
                var parentPath = CreatePointerFromSegments(pointerSegments);
                if (last == null && first == null)
                    return e;
                if (first == null)
                    return e.Concat([PatchOperation.Add(parentPath, JsonSerializer.SerializeToNode(last, options))]);
                if (last == null)
                    return e.Concat([PatchOperation.Remove(parentPath)]);


                var patches = first.CreatePatch(last, options).Operations;

                patches = patches.Select(p =>
                {
                    var newPath = parentPath.Combine(p.Path);
                    return CreatePatchOperation(p, newPath);
                }).ToArray();

                return e.Concat(patches);
            }).ToArray());
    }


    private static PatchOperation CreatePatchOperation(PatchOperation original, JsonPointer newPath)
    {
        return original.Op switch
        {
            OperationType.Add => PatchOperation.Add(newPath, original.Value),
            OperationType.Remove => PatchOperation.Remove(newPath),
            OperationType.Replace => PatchOperation.Replace(newPath, original.Value),
            // 🚨 Move/Copy take (FROM, PATH). Only the TARGET is re-rooted under parentPath.
            OperationType.Move => PatchOperation.Move(original.From, newPath),
            OperationType.Copy => PatchOperation.Copy(original.From, newPath),
            OperationType.Test => PatchOperation.Test(newPath, original.Value),
            _ => throw new InvalidOperationException($"Unsupported operation: {original.Op}")
        };
    }
    /// <summary>
    /// Encodes a key into a JSON-pointer path segment by serializing it to its JSON representation.
    /// </summary>
    /// <param name="segment">The key to encode; a null key yields an empty segment.</param>
    /// <param name="options">Serializer options used to serialize the key.</param>
    /// <returns>The encoded pointer segment.</returns>
    public static string EncodePointerSegment(string? segment, JsonSerializerOptions options)
    {
        if (segment is null) return string.Empty;

        var ret = JsonSerializer.Serialize(segment, options);
        // RFC 6901: escape ~ as ~0 and / as ~1
        return ret;
    }
    /// <summary>
    /// Decodes a JSON-pointer path segment back into a key object, unescaping RFC 6901 sequences
    /// (<c>~0</c>, <c>~1</c>) and deserializing the JSON value.
    /// </summary>
    /// <param name="segment">The pointer segment to decode; a null segment yields null.</param>
    /// <param name="options">Serializer options used to deserialize the key.</param>
    /// <returns>The decoded key object, or null when the segment is null.</returns>
    public static object? DecodePointerSegment(string? segment, JsonSerializerOptions options)
    {
        if (segment is null) return null;
        // 🚨 RFC 6901 §4 unescaping is ~1→/ BEFORE ~0→~, and it must be a SINGLE pass: the
        // previous `Replace("~0","~").Replace("~1","/")` decoded "a~01b" (an id containing the
        // literal "~1") to "a/b" instead of "a~1b", because the first pass produced a "~1" the
        // second pass then consumed. JsonPointerSegment.Decode walks the string once.
        segment = JsonPointerSegment.Decode(segment);
        var ret = JsonSerializer.Deserialize<object>(segment, options);
        return ret;
    }

}
