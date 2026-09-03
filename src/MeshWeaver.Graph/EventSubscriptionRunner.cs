using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// Fires durable <see cref="EventSubscription"/>s — the ONE background runner behind every deferred
/// reaction. Two paths keep it resilient across restarts:
/// <list type="bullet">
///   <item><b>Live</b> — subscribes to the mesh change feed; when a node matching a pending
///     <see cref="EventTriggerType.NodeChange"/> subscription's <see cref="EventSubscription.TriggerNodeType"/>
///     is changed and its field matches, the subscription fires.</item>
///   <item><b>Reconcile</b> — a live query over the outstanding subscriptions re-evaluates each on startup
///     (and whenever the set changes) against CURRENT state, so a trigger that happened while the runner
///     was down — e.g. the invitee signed up during a deploy — still fires. The durable state is the
///     subscription node itself (Pending → Fired), so nothing is lost.</item>
/// </list>
/// Continuations are idempotent (create-or-update grant, pin is a set-add, the terminal <c>Fired</c>
/// write gates re-entry), so the paths can't double-APPLY — but idempotent is not the same as free, and
/// the <c>Fired</c> gate only closes once that write has round-tripped. Until then every path is looking
/// at the same still-Pending snapshot, so they would each launch the continuation. A per-subscription
/// in-flight reservation (the <c>executing</c> registry) makes firing at-most-once instead: exactly one
/// continuation per subscription, no unobserved duplicate writes trailing behind it.
///
/// <para>🚨 The runner has NO ambient <c>AccessContext</c> (it's a background hosted service, not a
/// request). Every read AND write goes through <see cref="AsSystem{T}"/> — <c>Using(ImpersonateAsSystem,
/// Defer(factory))</c> — so the operation is both CONSTRUCTED and subscribed under the system identity.</para>
///
/// <para>This supersedes the former <c>ScheduledActionRunner</c>. On startup it migrates any legacy
/// <c>Admin/ScheduledAction/{id}</c> nodes (from before the generalization) into
/// <c>Admin/EventSubscription/{id}</c> so an in-flight invite is never dropped. Handles all three
/// trigger kinds: <see cref="EventTriggerType.NodeChange"/> (live change-feed + reconcile),
/// <see cref="EventTriggerType.Timer"/> (a one-shot <c>Observable.Timer</c> per pending subscription,
/// with a past <c>FireAt</c> firing on the next startup — restart-safe at-least-once), and
/// <see cref="EventTriggerType.NodeStatus"/> (a self-healing node-stream watch that fires when the
/// watched node's status reaches a resting value).</para>
/// </summary>
public sealed class EventSubscriptionRunner(
    IMessageHub hub,
    IMeshChangeFeed changeFeed,
    IMeshService meshService,
    AccessService accessService,
    ILogger<EventSubscriptionRunner>? logger = null) : IHostedService, IDisposable
{
    private readonly object gate = new();
    private IReadOnlyList<EventSubscription> pending = [];
    private readonly HashSet<string> migratedLegacyIds = [];
    // Live one-shot timers, keyed by subscription id — instance (never static), disposed on fire, on the
    // subscription leaving the pending set, and on runner stop. No leak.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IDisposable> timerSubs = new();
    // Live NodeStatus watches, same lifecycle contract as timerSubs.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IDisposable> statusSubs = new();
    // Live trigger-node watches — one per DISTINCT TriggerNodeType among pending NodeChange/Created
    // subscriptions (keyed by node type, NOT subscription id, so a handful of entries, never a per-
    // subscription registry leak). The change-feed-INDEPENDENT firing path: a synced query re-emits via
    // the storage layer (PG LISTEN/NOTIFY — silo-independent) when a matching node is created, so a
    // deferred invite fires even when the in-process/Orleans change feed's best-effort cross-silo relay
    // never delivers the User/Created event to this runner. Same lifecycle contract as timerSubs/statusSubs.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IDisposable> nodeChangeSubs = new();
    // 🚨 Subscriptions whose continuation is currently IN FLIGHT — the at-most-once guard for Execute.
    // EVERY firing path (change feed, cold-start reconcile, pending-set reconcile, trigger-node watch)
    // fires off the same `pending` snapshot, and a subscription only LEAVES that snapshot once its
    // terminal Fired write has round-tripped through the Admin partition. At startup all of those paths
    // evaluate within a few ms of each other — i.e. while the subscription is still Pending in every
    // snapshot — so ONE subscription launched N concurrent continuations, each issuing a duplicate
    // upsert of the same membership/grant node plus its own SetStatus write. Idempotence made those
    // writes harmless, never free: they are fire-and-forget requests nobody observes, they serialise
    // behind each other on the target node's hub, and they outlive whatever triggered them (they were
    // the CreateOrUpdateNodeRequest callbacks still pending at test teardown). The remedy is the same
    // instance-scoped TryAdd reservation the timer / status / trigger-node registries already use:
    // reserved before the chain starts, released on failure so a later emission retries, and pruned
    // when the subscription leaves the pending set — so it stays bounded by the pending set rather than
    // growing for the runner's whole uptime.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> executing = new();
    private IDisposable? pendingSub;
    private IDisposable? feedSub;
    private IDisposable? legacySub;
    // One-shot cold-start reconcile seeded from an authoritative storage read (see StartAsync) — disposed on stop.
    private IDisposable? startupSub;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Fold any legacy ScheduledAction nodes into EventSubscription nodes so nothing in-flight is
        // lost when this runner replaces ScheduledActionRunner.
        MigrateLegacyScheduledActions();

        // Live snapshot of outstanding subscriptions; re-emits on add / fire / cancel. Reading the Admin
        // partition needs an identity → system. (Constant query id: one registry entry, no leak.)
        pendingSub = AsSystem(() => hub.GetWorkspace().GetQuery("event-subscriptions",
                $"path:{EventSubscriptionNodeType.Namespace} scope:children nodeType:{EventSubscriptionNodeType.NodeType} select:path,id,namespace,name,nodeType,content"))
            .Subscribe(nodes =>
            {
                // 🚨 ReadSubscription, NEVER `n.Content as EventSubscription` (issue #1392). This
                // list IS the candidate set for every firing path — change feed, trigger-node
                // watch, Timer, NodeStatus — so a soft-cast that yields null does not degrade the
                // runner, it SILENCES it: an empty pending set means nothing can ever fire, with
                // no error anywhere. That is exactly what ran in production, because the mesh hub
                // resolving this query had no EventSubscription registration (now fixed in
                // WithGraphTypes) and handed back untyped JsonElements. The sibling cold-start
                // path below already read it tolerantly; the two must agree.
                var list = (nodes ?? [])
                    .Select(ReadSubscription)
                    .Where(s => s is { Status: EventSubscriptionStatus.Pending })
                    .Select(s => s!)
                    .ToList();
                lock (gate) pending = list;

                // Reconcile NodeChange/Created subscriptions against current state (catch missed triggers).
                foreach (var s in list.Where(s => s is
                             { TriggerType: EventTriggerType.NodeChange, TriggerKind: MeshChangeKind.Created }))
                    Reconcile(s);

                // Schedule one-shot timers + NodeStatus watches for pending subscriptions; cancel any whose
                // subscription left the pending set (fired / cancelled elsewhere).
                foreach (var s in list.Where(s => s is { TriggerType: EventTriggerType.Timer, FireAt: not null }))
                    ScheduleTimer(s);
                foreach (var s in list.Where(s => s is { TriggerType: EventTriggerType.NodeStatus, WatchPath.Length: > 0 }))
                    WatchNodeStatus(s);

                // Change-feed-INDEPENDENT reconcile: keep a live trigger-node query per distinct node type
                // among the pending Created subscriptions. It re-emits (via PG LISTEN/NOTIFY, silo-agnostic)
                // when a matching node is created even if the change-feed event never reaches this runner —
                // the memex 2026-07-20 stranded-invite root cause (cross-silo relay dropped the create).
                var neededNodeTypes = list
                    .Where(s => s is { TriggerType: EventTriggerType.NodeChange, TriggerKind: MeshChangeKind.Created,
                        TriggerNodeType.Length: > 0 })
                    .Select(s => s.TriggerNodeType!)
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var nodeType in neededNodeTypes)
                    WatchTriggerNodeType(nodeType);
                foreach (var nodeType in nodeChangeSubs.Keys.Where(nt => !neededNodeTypes.Contains(nt)).ToList())
                    if (nodeChangeSubs.TryRemove(nodeType, out var d))
                        d.Dispose();

                var pendingIds = list.Select(s => s.Id).ToHashSet();
                foreach (var (subs, _) in new[] { (timerSubs, 0), (statusSubs, 0) })
                    foreach (var id in subs.Keys.Where(id => !pendingIds.Contains(id)).ToList())
                        if (subs.TryRemove(id, out var d))
                            d.Dispose();
                // The in-flight reservation is keyed the same way: a subscription that has left the
                // pending set reached a terminal state (Fired / Failed / Cancelled) and can never fire
                // again — every firing path draws its candidates from `pending` — so drop its entry.
                // That keeps `executing` bounded by the pending set instead of the runner's uptime.
                foreach (var id in executing.Keys.Where(id => !pendingIds.Contains(id)).ToList())
                    executing.TryRemove(id, out _);
            }, ex => logger?.LogWarning(ex, "Event-subscriptions query failed"));

        // 🚨 Cold-start authoritative reconcile — the fix for the memex 2026-07-20 stranded-invite incident.
        // The live pendingSub above reads the WORKSPACE cache (GetQuery): on a cold start it is EMPTY until
        // the Admin partition syncs into the workspace, and it does NOT re-emit when the partition later
        // warms — only on a subsequent write. So after every deploy restart the runner's pending set stayed
        // empty and NOTHING reconciled until an unrelated Admin/EventSubscription write happened to nudge
        // the query; an invited, already-onboarded user's Pending grants were stranded for hours (bari: his
        // User node existed and his AddToGroup/GrantSpaceAccess subs were Pending, yet none fired after the
        // roll until a write re-triggered the reconcile). IMeshService.Query is a FRESH storage read that
        // does NOT wait on the workspace cache, so it returns the outstanding subscriptions even on a cold
        // start — seed the reconcile once from it. Every already-matchable Created subscription fires now,
        // and each fire's terminal Fired-write re-emits pendingSub (warm) so the live path takes over.
        // One-shot + idempotent (the Fired write gates re-entry; continuations are create-or-update).
        startupSub = AsSystem(() => meshService.Query<MeshNode>(MeshQueryRequest.FromQuery(
                    $"path:{EventSubscriptionNodeType.Namespace} scope:children nodeType:{EventSubscriptionNodeType.NodeType}"))
                .Where(c => c.ChangeType == QueryChangeType.Initial)
                .Select(c => c.Items)
                .Take(1))
            .Subscribe(items =>
            {
                foreach (var node in items ?? [])
                    if (ReadSubscription(node) is
                        { Status: EventSubscriptionStatus.Pending,
                          TriggerType: EventTriggerType.NodeChange, TriggerKind: MeshChangeKind.Created } s)
                        Reconcile(s);
            }, ex => logger?.LogWarning(ex, "Startup authoritative reconcile failed"));

        // Live: fire on the actual change event.
        feedSub = changeFeed.Subscribe(OnChange);
        return Task.CompletedTask;
    }

    private void OnChange(MeshChangeEvent e)
    {
        List<EventSubscription> candidates;
        lock (gate)
            candidates = pending
                .Where(s => s.TriggerType == EventTriggerType.NodeChange
                            && s.TriggerKind == e.Kind
                            && string.Equals(s.TriggerNodeType, e.NodeType, StringComparison.Ordinal))
                .ToList();
        if (candidates.Count == 0)
            return;

        // Read the triggering node once (system identity); evaluate each candidate's field match.
        AsSystem(() => hub.GetMeshNode(e.Path, TimeSpan.FromSeconds(10))).Subscribe(node =>
        {
            if (node is null)
                return;
            foreach (var s in candidates)
                if (Matches(s, node))
                    Execute(s, node);
        }, ex => logger?.LogWarning(ex, "Reading triggering node {Path} failed", e.Path));
    }

    private void Reconcile(EventSubscription subscription)
    {
        // A trigger type's instances live wherever they were written, so the reconcile is
        // mesh-wide by nature and says so (#3202 — fan-out is opt-in). A type with a registered
        // routing pin (User → Auth) is still served from its one schema: the planner narrows a
        // declared fan-out by the pin before it enumerates.
        var query = MeshWideQuery.Declare($"nodeType:{subscription.TriggerNodeType}");
        if (subscription is { MatchField.Length: > 0, MatchValue.Length: > 0 })
            query += $" content.{subscription.MatchField}:{subscription.MatchValue}";

        // One-shot lookup via Query<T> (NOT the workspace GetQuery, which caches by id for the workspace
        // lifetime — a per-subscription id would leak a registry entry each time). Take the initial
        // snapshot and stop.
        AsSystem(() => meshService.Query<MeshNode>(MeshQueryRequest.FromQuery(query))
                .Where(c => c.ChangeType == QueryChangeType.Initial)
                .Select(c => c.Items)
                .Take(1))
            .Subscribe(items =>
            {
                var node = items.FirstOrDefault(n => Matches(subscription, n));
                if (node is not null)
                    Execute(subscription, node);
            }, ex => logger?.LogWarning(ex, "Reconcile query for subscription {Id} failed", subscription.Id));
    }

    /// <summary>
    /// Establishes a LIVE query over every node of <paramref name="nodeType"/> and reconciles the pending
    /// <see cref="EventTriggerType.NodeChange"/>/<see cref="MeshChangeKind.Created"/> subscriptions of that
    /// type against each emission — the reliable, change-feed-INDEPENDENT firing path.
    ///
    /// <para><see cref="OnChange"/> fires the instant a node is written, but the change feed's cross-silo
    /// relay is best-effort: on a distributed portal a <c>User</c> created in its own partition hub (another
    /// silo) may never reach this runner, so the deferred invite strands until the next restart's reconcile
    /// (the memex 2026-07-20 "bari onboarded but got no access" incident — all six of his pending
    /// subscriptions stayed Pending). A synced query re-emits through the storage layer (PG LISTEN/NOTIFY),
    /// which is silo-independent, so this watch fires the subscription regardless of change-feed delivery.
    /// It supplements — does not replace — the feed + startup reconcile; all three are idempotent (the
    /// terminal <c>Fired</c> write gates re-entry and every continuation is a create-or-update), so they
    /// cannot double-apply. One watch per node type (a small bounded set; the dictionary key is the node
    /// type, not the subscription id, so no per-subscription registry leak), disposed when no pending
    /// Created subscription needs it and on runner stop.</para>
    /// </summary>
    private void WatchTriggerNodeType(string nodeType)
    {
        if (nodeChangeSubs.ContainsKey(nodeType))
            return;
        var slot = new System.Reactive.Disposables.SingleAssignmentDisposable();
        if (!nodeChangeSubs.TryAdd(nodeType, slot))
            return;   // lost the race — another emission just established this node type
        slot.Disposable = AsSystem(() => meshService.Query<MeshNode>(
                // Same mesh-wide shape as Reconcile, for the same reason (#3202).
                MeshQueryRequest.FromQuery(MeshWideQuery.OfType(nodeType))))
            // Every ChangeType EXCEPT Removed means "a matching node exists now" and is a valid
            // existence-based reconcile trigger: Initial/Reset carry the full snapshot, Added a just-
            // created invitee (the deferred-grant case), Updated an existing one re-emitted. EXCLUDE
            // Removed — its Items carries the DELETED node (Items is a delta for Added/Updated/Removed,
            // the full set only for Initial/Reset), and a Created subscription must never fire for a node
            // being deleted. Repeated fires across emissions are harmless: the terminal Fired write drops
            // the subscription from `pending` and every continuation is a create-or-update.
            .Where(c => c.ChangeType != QueryChangeType.Removed)
            .Select(c => c.Items)
            .Subscribe(
                items =>
                {
                    List<EventSubscription> candidates;
                    lock (gate)
                        candidates = pending
                            .Where(s => s is { TriggerType: EventTriggerType.NodeChange, TriggerKind: MeshChangeKind.Created }
                                        && string.Equals(s.TriggerNodeType, nodeType, StringComparison.Ordinal))
                            .ToList();
                    foreach (var s in candidates)
                    {
                        var node = (items ?? []).FirstOrDefault(n => Matches(s, n));
                        if (node is not null)
                            Execute(s, node);
                    }
                },
                ex => logger?.LogWarning(ex, "Trigger-node watch for {NodeType} failed", nodeType));
    }

    /// <summary>
    /// Reads a node's content as an <see cref="EventSubscription"/> through the sanctioned
    /// bad-data-tolerant accessor: a typed instance (the workspace <c>GetQuery</c> path) passes
    /// through, a raw <c>JsonElement</c>/<c>JsonNode</c> (the storage <c>IMeshService.Query</c>
    /// path the cold-start seed reads from, and the degraded GetQuery shape) is deserialized, and
    /// anything unconvertible returns null — logged loud with the node path, never swallowed.
    /// EVERY read of a subscription node goes through here; a bare <c>as</c> is the trap-door that
    /// emptied the pending set in production (#1392).
    /// </summary>
    private EventSubscription? ReadSubscription(MeshNode node)
        => node.ContentAs<EventSubscription>(hub.JsonSerializerOptions, logger);

    private bool Matches(EventSubscription subscription, MeshNode node)
    {
        if (!string.Equals(subscription.TriggerNodeType, node.NodeType, StringComparison.Ordinal))
            return false;
        if (subscription.MatchField is not { Length: > 0 } field)
            return true;
        var actual = EventSubscriptionOps.ReadContentField(node, field, hub.JsonSerializerOptions);
        return string.Equals(actual, subscription.MatchValue, StringComparison.OrdinalIgnoreCase);
    }

    // A NodeChange trigger's node id IS the subject (a User node's path IS the userId).
    private void Execute(EventSubscription subscription, MeshNode triggerNode)
        => Execute(subscription, triggerNode.Id);

    private void Execute(EventSubscription subscription, string userId)
    {
        // At-most-once per subscription: reserve the id BEFORE building the chain (see `executing`).
        // Whichever firing path gets here first owns this subscription's continuation; the others —
        // which are looking at the same still-Pending snapshot — must not launch a duplicate.
        if (!executing.TryAdd(subscription.Id, 0))
            return;
        BuildContinuation(subscription, userId)
            // A REPEATER has no terminal state: instead of Fired it records its next slot and stays
            // Pending. That write is the durability — a repeater that fired without recording the
            // next occurrence would re-fire from the stale one on the next reboot, which for a
            // nightly job means running it again every time a pod restarts.
            .SelectMany(_ => subscription.RepeatEvery is { } every
                ? AsSystem(() => EventSubscriptionOps.RearmTimer(
                    hub, EventSubscriptionNodeType.Path(subscription.Id), NextOccurrence(subscription, every)))
                : AsSystem(() => EventSubscriptionOps.SetStatus(
                    hub, EventSubscriptionNodeType.Path(subscription.Id), EventSubscriptionStatus.Fired)))
            .Subscribe(
                // `fired` is named, not `_`: a discard here would shadow the `out _` below and bind
                // it to this lambda's MeshNode parameter instead.
                fired =>
                {
                    // The reservation is per FIRING, not per subscription: a repeater must be able to
                    // run again, so release it once this occurrence has recorded its next slot.
                    if (subscription.RepeatEvery is not null)
                        executing.TryRemove(subscription.Id, out _);
                    logger?.LogInformation(
                        "Event subscription {Id} fired: {Continuation} {Role} for {User} on {Target}",
                        subscription.Id, subscription.ContinuationType, subscription.Role, userId,
                        subscription.TargetPath);
                },
                ex =>
                {
                    // Release the reservation so a later pending-set emission can retry a TRANSIENT
                    // failure — the same rule MigrateLegacyScheduledActions applies to its own id guard.
                    // (A permanent failure writes Failed below and leaves the pending set anyway.)
                    executing.TryRemove(subscription.Id, out _);
                    logger?.LogWarning(ex, "Event subscription {Id} failed", subscription.Id);
                    AsSystem(() => EventSubscriptionOps.SetStatus(
                            hub, EventSubscriptionNodeType.Path(subscription.Id), EventSubscriptionStatus.Failed, ex.Message))
                        .Subscribe(_ => { }, _ => { });
                });
    }

    /// <summary>
    /// The next occurrence of a repeating timer: the first multiple of <paramref name="every"/> after
    /// its previous slot that is still in the FUTURE.
    ///
    /// <para>Catch-up is deliberately not replayed. A nightly job whose install was down for a week
    /// must run once when it comes back, not seven times in a row — the point of a recurring job is
    /// the current state, and six re-runs of yesterday's ingest cost real API quota to arrive at the
    /// same answer.</para>
    /// </summary>
    private static DateTimeOffset NextOccurrence(EventSubscription subscription, TimeSpan every)
    {
        var now = DateTimeOffset.UtcNow;
        var next = (subscription.FireAt ?? now) + every;
        if (every <= TimeSpan.Zero)
            return now + TimeSpan.FromMinutes(1);   // a nonsense interval must not become a hot loop
        while (next <= now)
            next += every;
        return next;
    }

    /// <summary>
    /// Schedules a one-shot timer for a pending <see cref="EventTriggerType.Timer"/> subscription
    /// (idempotent per id — the slot is reserved before subscribing, so two pending-set emissions can't
    /// double-schedule). A <see cref="EventSubscription.FireAt"/> already in the past fires immediately,
    /// which — since the subscription node is durable and its <c>Pending → Fired</c> gates re-entry —
    /// gives restart-safe at-least-once firing (a timer due during downtime fires on the next boot).
    /// </summary>
    private void ScheduleTimer(EventSubscription subscription)
    {
        if (timerSubs.ContainsKey(subscription.Id))
            return;
        var slot = new System.Reactive.Disposables.SingleAssignmentDisposable();
        if (!timerSubs.TryAdd(subscription.Id, slot))
            return;   // lost the race — another emission just scheduled this id
        var delay = subscription.FireAt!.Value - DateTimeOffset.UtcNow;
        if (delay < TimeSpan.Zero)
            delay = TimeSpan.Zero;
        slot.Disposable = Observable.Timer(delay)
            // 🚨 RE-READ AT FIRE TIME. The subscription captured when this timer was ARMED is a
            // snapshot, and a timer's whole nature is that time passes between arming and firing —
            // during which the subscription can be edited, or cancelled outright.
            //
            // Both failures were observed in production on 2026-08-20, on one post:
            //   • it was CANCELLED at 05:58:53 (its post stopped being schedulable) and fired anyway
            //     at 06:00:00.005, because cancelling writes the NODE and never touched this
            //     already-scheduled in-memory timer; and
            //   • it fired reporting "names no CreatedBy" while the stored node plainly had one —
            //     an operator had set it hours after the timer armed, and the closure still held the
            //     value from arming time.
            // A scheduled job that ignores every edit made after it was scheduled is not a schedule,
            // it is a recording.
            .SelectMany(_ => AsSystem(() => hub.GetWorkspace()
                    .GetMeshNodeStream(EventSubscriptionNodeType.Path(subscription.Id)))
                .Take(1)
                .Select(node => node?.ContentAs<EventSubscription>(hub.JsonSerializerOptions))
                .Catch<EventSubscription?, Exception>(ex =>
                {
                    // Unreadable at fire time: fall back to the snapshot rather than silently skip.
                    // A missed publish is worse than one made on slightly stale data, and the
                    // continuation re-checks its own target anyway.
                    logger?.LogWarning(ex,
                        "Could not re-read subscription {Id} at fire time; using the armed snapshot",
                        subscription.Id);
                    return Observable.Return<EventSubscription?>(subscription);
                }))
            .Subscribe(current =>
            {
                if (timerSubs.TryRemove(subscription.Id, out var d))
                    d.Dispose();

                var live = current ?? subscription;
                if (live.Status != EventSubscriptionStatus.Pending)
                {
                    logger?.LogInformation(
                        "Timer {Id} reached its slot but the subscription is {Status} — not firing.",
                        subscription.Id, live.Status);
                    return;
                }
                Execute(live, live.SubjectId ?? "");
            });
    }

    /// <summary>
    /// Watches the node at <see cref="EventSubscription.WatchPath"/> and fires when its
    /// <see cref="EventSubscription.StatusField"/> enters <see cref="EventSubscription.RestingValues"/> —
    /// after first seeing a non-resting (active) value when <see cref="EventSubscription.RequireActiveFirst"/>
    /// (the delegation "saw-running → resting" semantics). Idempotent per id, self-disposing on fire, and
    /// self-healing: uses <see cref="ActivityControlPlaneExtensions.SubscribeWithReEstablish"/>, which
    /// re-establishes on a transient fault and terminally STOPS (no storm) when the watched node is gone.
    /// On reboot the pending-set reconcile re-attaches the watch and a node that reached its resting state
    /// during downtime fires immediately (restart-safe).
    /// </summary>
    private void WatchNodeStatus(EventSubscription subscription)
    {
        if (statusSubs.ContainsKey(subscription.Id))
            return;
        var slot = new System.Reactive.Disposables.SingleAssignmentDisposable();
        if (!statusSubs.TryAdd(subscription.Id, slot))
            return;   // lost the race
        var sawActive = false;
        var fired = false;
        var statusField = subscription.StatusField is { Length: > 0 } f ? f : "Status";
        // SingleAssignmentDisposable makes onNext firing synchronously-on-subscribe safe: slot.Dispose()
        // marks it disposed, and the later `slot.Disposable = …` assignment then disposes the watch too.
        slot.Disposable = ActivityControlPlaneExtensions.SubscribeWithReEstablish<MeshNode>(
            () => AsSystem(() => hub.GetWorkspace().GetMeshNodeStream(subscription.WatchPath!)),
            node =>
            {
                if (fired)
                    return;
                var status = node?.Content is null
                    ? null
                    : EventSubscriptionOps.ReadContentField(node, statusField, hub.JsonSerializerOptions);
                if (status is null)
                    return;
                var resting = subscription.RestingValues.Any(v =>
                    string.Equals(v, status, StringComparison.OrdinalIgnoreCase));
                if (!resting)
                {
                    sawActive = true;
                    return;
                }
                if (subscription.RequireActiveFirst && !sawActive)
                    return;   // initial replayed-resting — the node never ran; wait for an active state first
                fired = true;
                statusSubs.TryRemove(subscription.Id, out _);
                slot.Dispose();
                Execute(subscription, subscription.SubjectId ?? "");
            },
            hub.Address,
            logger,
            $"EventSubscription.NodeStatus[{subscription.Id}]");
    }

    private IObservable<MeshNode> BuildContinuation(EventSubscription subscription, string userId) =>
        subscription.ContinuationType switch
        {
            EventContinuationType.GrantSpaceAccess
                when subscription is { TargetPath.Length: > 0, Role.Length: > 0 } && !string.IsNullOrEmpty(userId) =>
                AsSystem(() => EventSubscriptionOps.Grant(meshService, userId, subscription.TargetPath!, subscription.Role!))
                    .SelectMany(g => subscription.Pin
                        ? AsSystem(() => EventSubscriptionOps.Pin(hub, userId, subscription.TargetPath!)).Select(_ => g)
                        : Observable.Return(g)),
            // Membership, plus — when the invite chose a role — the matching AccessAssignment on the group
            // (groups aren't publicly readable; the grant is what lets the new member see the group).
            EventContinuationType.AddToGroup
                when subscription is { TargetPath.Length: > 0 } && !string.IsNullOrEmpty(userId) =>
                AsSystem(() => EventSubscriptionOps.AddToGroup(meshService, userId, subscription.TargetPath!))
                    .SelectMany(m => subscription.Role is { Length: > 0 }
                        ? AsSystem(() => EventSubscriptionOps.Grant(
                                meshService, userId, subscription.TargetPath!, subscription.Role!))
                            .Select(_ => m)
                        : Observable.Return(m)),
            // The general scheduled job: run a Code node. Handled HERE rather than through the
            // extension point because it needs nothing this assembly lacks — ExecuteScriptRequest is
            // a contract type and the reply is observed reactively, so no module has to own it.
            //
            // 🚨 Observe, not Post. Post is fire-and-forget: the runner would mark the subscription
            // Fired the instant the message left, whether the script ran, threw or was never
            // delivered — and a nightly job that silently stopped running is exactly the failure
            // this whole mechanism exists to make visible. Observing the reply ties the
            // subscription's Fired/Failed to what the script actually did.
            EventContinuationType.RunScript when subscription is { TargetPath.Length: > 0 } =>
                AsSystem(() => hub.Observe<ExecuteScriptResponse>(
                        new ExecuteScriptRequest(),
                        o => o.WithTarget(new Address(subscription.TargetPath!)))
                    .Take(1)
                    .Timeout(TimeSpan.FromMinutes(30))
                    .Select(_ => new MeshNode(subscription.TargetPath!))),

            // Continuations this assembly cannot implement — their effects live ABOVE it in the
            // reference graph (PublishSocialPost is owned by MeshWeaver.Social, which references
            // Graph). The owning module registers an IEventContinuationHandler; nothing is silently
            // skipped, because an unhandled type still falls through to the throw below.
            _ => ExternalContinuation(subscription, userId),
        };

    /// <summary>
    /// Dispatches to the <see cref="IEventContinuationHandler"/> registered for this continuation.
    /// Resolved per firing (never cached): handlers are registered by modules, and a cached null taken
    /// before a module's registration landed would wedge every later subscription of that type for the
    /// life of the process.
    ///
    /// <para>Both failure modes name themselves, because both are configuration mistakes that would
    /// otherwise present as "the thing just never happened":</para>
    /// <list type="bullet">
    ///   <item><b>None registered</b> — the owning module was not composed into this host (a missing
    ///   <c>AddSocial</c>, say). The generic "unsupported subscription" wording sent readers looking
    ///   for a bad subscription instead of an absent registration.</item>
    ///   <item><b>More than one</b> — ambiguous, so it FAILS rather than picking one. Taking the first
    ///   match would make behaviour depend on registration order, which is nondeterministic across
    ///   hosts and hides the duplicate indefinitely.</item>
    /// </list>
    /// </summary>
    private IObservable<MeshNode> ExternalContinuation(EventSubscription subscription, string userId)
    {
        var handlers = hub.ServiceProvider.GetServices<IEventContinuationHandler>()
            .Where(h => h.ContinuationType == subscription.ContinuationType)
            .ToList();

        if (handlers.Count == 1)
            return AsSystem(() => handlers[0].Execute(subscription, userId));

        return Observable.Throw<MeshNode>(new InvalidOperationException(handlers.Count == 0
            ? $"Event subscription {subscription.Id} has continuation "
              + $"{subscription.ContinuationType}, which no IEventContinuationHandler is registered "
              + "for. The module that owns it is not composed into this host — nothing will ever fire "
              + "this subscription until it is."
            : $"Event subscription {subscription.Id} has continuation "
              + $"{subscription.ContinuationType}, which {handlers.Count} IEventContinuationHandlers "
              + $"claim ({string.Join(", ", handlers.Select(h => h.GetType().Name))}). Exactly one must "
              + "be registered — refusing to pick one, because which arrived first is not a decision "
              + "this runner can make correctly."));
    }

    /// <summary>
    /// Migrates legacy <c>Admin/ScheduledAction/{id}</c> nodes into equivalent
    /// <c>Admin/EventSubscription/{id}</c> nodes (then deletes the legacy node). A LIVE query, not a
    /// one-shot <c>Take(1)</c>: the query index is eventually consistent, so a legacy node written just
    /// before this runner booted may not be in the FIRST emission — the live subscription migrates it
    /// when the index catches up. Each id is migrated at most once (the <see cref="migratedLegacyIds"/>
    /// guard), and the migration is idempotent (upsert by id + delete). The subscription is owned by the
    /// hosted-service lifetime (disposed in <see cref="Dispose"/>) — not a leak. Runs as system.
    /// </summary>
    private void MigrateLegacyScheduledActions()
    {
        legacySub = AsSystem(() => hub.GetWorkspace().GetQuery("event-subscriptions-legacy",
                $"path:{ScheduledActionNodeType.Namespace} scope:children nodeType:{ScheduledActionNodeType.NodeType} select:path,id,namespace,name,nodeType,content"))
            .Subscribe(nodes =>
            {
                foreach (var node in nodes ?? [])
                {
                    // Same rule as ReadSubscription: the tolerant accessor, never `is not X` — the
                    // legacy node is read on a hub that never writes one, so a soft-cast would
                    // silently migrate nothing and strand the in-flight invite it exists to save.
                    if (node.ContentAs<ScheduledAction>(hub.JsonSerializerOptions, logger) is not { } legacy)
                        continue;
                    lock (gate)
                        if (!migratedLegacyIds.Add(legacy.Id))
                            continue;   // in-flight or done — don't double-process this id
                    var migrated = FromLegacy(legacy);
                    AsSystem(() => EventSubscriptionOps.CreateSubscription(meshService, migrated)
                            .SelectMany(_ => meshService.DeleteNode(ScheduledActionNodeType.Path(legacy.Id))))
                        .Subscribe(
                            _ => logger?.LogInformation("Migrated legacy ScheduledAction {Id} → EventSubscription", legacy.Id),
                            ex =>
                            {
                                logger?.LogWarning(ex, "Migrating legacy ScheduledAction {Id} failed", legacy.Id);
                                // Release the id so a later live-query emission retries — a transient store
                                // failure must not permanently strand the legacy node.
                                lock (gate) migratedLegacyIds.Remove(legacy.Id);
                            });
                }
            }, ex => logger?.LogWarning(ex, "Legacy ScheduledAction migration query failed"));
    }

    /// <summary>Maps a legacy <see cref="ScheduledAction"/> to the equivalent <see cref="EventSubscription"/>
    /// (a NodeChange trigger + GrantSpaceAccess continuation — the only shape the legacy type had).</summary>
    private static EventSubscription FromLegacy(ScheduledAction a) => new()
    {
        Id = a.Id,
        TriggerType = EventTriggerType.NodeChange,
        TriggerNodeType = a.TriggerNodeType,
        TriggerKind = a.TriggerKind,
        MatchField = a.MatchField,
        MatchValue = a.MatchValue,
        ContinuationType = EventContinuationType.GrantSpaceAccess,
        TargetPath = a.TargetPath,
        Role = a.Role,
        Pin = a.Pin,
        Status = a.Status switch
        {
            ScheduledActionStatus.Fired => EventSubscriptionStatus.Fired,
            ScheduledActionStatus.Failed => EventSubscriptionStatus.Failed,
            ScheduledActionStatus.Cancelled => EventSubscriptionStatus.Cancelled,
            _ => EventSubscriptionStatus.Pending,
        },
        CreatedBy = a.CreatedBy,
        CreatedAt = a.CreatedAt,
        FiredAt = a.FiredAt,
        LastError = a.LastError,
    };

    /// <summary>Runs a freshly-constructed <paramref name="factory"/> operation under the system
    /// identity — the runner has no ambient AccessContext, and both reads and writes capture identity
    /// at construction/subscribe. <c>Defer</c> moves construction inside the impersonation scope.</summary>
    private IObservable<T> AsSystem<T>(Func<IObservable<T>> factory)
        => accessService.RunAsSystem(factory);

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        pendingSub?.Dispose();
        feedSub?.Dispose();
        legacySub?.Dispose();
        startupSub?.Dispose();
        foreach (var subs in new[] { timerSubs, statusSubs, nodeChangeSubs })
            foreach (var id in subs.Keys.ToList())
                if (subs.TryRemove(id, out var d))
                    d.Dispose();
        executing.Clear();
    }
}
