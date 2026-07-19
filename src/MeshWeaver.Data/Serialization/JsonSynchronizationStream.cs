using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Patch;
using Json.Pointer;
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
    // Mirror of MeshWeaver.Mesh.Security.WellKnownUsers.System — Data sits below
    // Mesh.Contract in the project graph and cannot reference it. Same literal
    // recognized by AccessService.ImpersonateAsSystem and PostgreSqlMeshQuery's
    // System-bypass short-circuit.
    private const string SystemUserId = "system-security";

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
        if (hub.RunLevel > MessageHubRunLevel.Started)
            throw new ObjectDisposedException($"ParentHub {hub.Address} is disposing, cannot create stream for {reference}.");

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
        var ambient = accessService?.Context ?? accessService?.CircuitContext;
        var isRealUser = ambient is not null
            && !string.IsNullOrEmpty(ambient.ObjectId)
            && !LooksLikeHubPrincipal(ambient.ObjectId);
        var identityForSubscribe = isRealUser ? ambient!.ObjectId : SystemUserId;

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
                            });
                }
            }

            var heartbeatInterval = hub.ServiceProvider
                .GetService<Microsoft.Extensions.Options.IOptions<SyncStreamOptions>>()
                ?.Value?.HeartbeatInterval ?? TimeSpan.FromSeconds(45);
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
            sub.Disposable = Observable.Interval(heartbeatInterval)
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
                    // scan) until the hub's action block/liveness probe stalled: the doc-crawl / atioz
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
            // starved atioz's hubs (one hot ApiToken node written per request reached version
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

    internal static ISynchronizationStream CreateSynchronizationStream<TReduced, TReference>(
        this IWorkspace workspace,
        IMessageDelivery<SubscribeRequest> delivery
)
    where TReference : WorkspaceReference
    {
        var hub = workspace.Hub;
        var logger = GetLogger(hub.ServiceProvider);
        var request = delivery.Message with { Subscriber = delivery.Sender };

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
        //             reduced.Host.GetWorkspace().RequestChange(e, null, null);
        //         })
        // );

        return reduced;
    }
    private static IObservable<TChange?> ToDataChanged<TReduced, TChange>(
        this ISynchronizationStream<TReduced> stream, Func<ChangeItem<TReduced>, bool> predicate) where TChange : JsonChange =>
        stream
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
                    return (TChange?)Activator.CreateInstance(
                        typeof(TChange),
                        stream.ClientId,
                        x.Version,
                        new RawJson(currentJson.ToString() ?? string.Empty),
                        ChangeType.Full,
                        x.ChangedBy ?? string.Empty);
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
                        return (TChange?)Activator.CreateInstance(
                            typeof(TChange),
                            stream.ClientId,
                            x.Version,
                            new RawJson(currentJson.Value.ToString() ?? string.Empty),
                            ChangeType.Full,
                            x.ChangedBy ?? string.Empty);
                    }
                    stream.Set(currentJson);
                    return (TChange?)Activator.CreateInstance
                    (
                        typeof(TChange),
                        stream.ClientId,
                        x.Version,
                        new RawJson(patchJson),
                        x.ChangeType,
                        x.ChangedBy ?? string.Empty
                    );
                }


            });





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
                    // For other operations, fall back to the library's Apply
                    var parsedPath = JsonPointer.Parse(pathString);
                    var operation = op switch
                    {
                        "move" when opElement.TryGetProperty("from", out var fromEl) =>
                            PatchOperation.Move(parsedPath, JsonPointer.Parse(fromEl.GetString()!)),
                        "copy" when opElement.TryGetProperty("from", out var fromEl) =>
                            PatchOperation.Copy(parsedPath, JsonPointer.Parse(fromEl.GetString()!)),
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
            return (JsonSerializer.SerializeToElement(result, options), fallbackPatch);
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
            OperationType.Move => PatchOperation.Move(newPath, original.From),
            OperationType.Copy => PatchOperation.Copy(newPath, original.From),
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
        // RFC 6901: escape ~ as ~0 and / as ~1

        segment = segment.Replace("~0", "~").Replace("~1", "/");
        var ret = JsonSerializer.Deserialize<object>(segment, options);
        return ret;
    }

}
