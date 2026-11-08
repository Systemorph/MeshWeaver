using System.Reactive.Disposables;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
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
///
/// <para>A NodeChange subscription that names an exact <see cref="EventSubscription.TriggerPath"/>
/// fires on that path's <see cref="MeshChangeKind.Deleted"/> alone — proposed by the change feed, by a
/// live listing of its scope (its parent's children, or a top-level path itself), or by the
/// once-per-lifetime catch-up — and every proposal is gated
/// on an authoritative "it is gone" read (<see cref="ConfirmGoneAndFire"/>), so it never fires for a
/// path that still exists.</para>
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
    // Exact-path Deleted subscriptions (EventSubscription.TriggerPath) already checked against current
    // state since this runner started — keyed by subscription id, pruned with the pending set, so each
    // is reconciled ONCE per runner lifetime (on startup, or when it first appears) and not on every
    // unrelated Admin/EventSubscription write. Released when the check could not be completed, so a
    // later emission retries it.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> deletionChecked = new();
    // Live listings, one per DISTINCT deletion scope of a pending exact-path Deleted subscription — its
    // parent's children, or the path itself for a top-level path (keyed by that scope's query, so bounded
    // by the number of scopes, not of subscriptions) — the change-feed-INDEPENDENT path for a delete, the
    // twin of nodeChangeSubs. Same lifecycle contract.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IDisposable> deletionWatchSubs = new();
    // 🚨 The ONE confirm-and-fire per exact-path subscription. Every proposal (feed, scope watch,
    // catch-up) reserves the subscription's slot with TryAdd BEFORE its read, so two proposals can never
    // run two reads — the slower of which used to answer after the Fired write had pruned `executing`
    // and deliver a second time. The slot owns the read's subscription (disposed on stop, so a reply
    // that lands after StopAsync starts nothing). A read that settles without firing releases the slot
    // so a later proposal reads again; a slot that FIRED is held until the subscription leaves the
    // pending set. Instance-scoped, bounded by the pending set.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ConfirmSlot> confirmSubs = new();
    // The catch-up listings in flight — owned, so a stop disposes them and none issues a read after it.
    private readonly CompositeDisposable catchUpSubs = new();
    // Set by Dispose: every callback that could start work checks it, because a reply can be in the
    // scheduler's hands when its subscription is disposed.
    private volatile bool disposed;

    /// <summary>One confirm-and-fire in flight (or fired and held) for one exact-path subscription.</summary>
    private sealed class ConfirmSlot : IDisposable
    {
        public SingleAssignmentDisposable Read { get; } = new();
        /// <summary>The read confirmed "gone" and handed the subscription to Execute.</summary>
        public volatile bool Fired;
        public void Dispose() => Read.Dispose();
    }
    private IDisposable? pendingSub;
    private IDisposable? feedSub;
    private IDisposable? legacySub;
    // One-shot cold-start reconcile seeded from an authoritative storage read (see StartAsync) — disposed on stop.
    private IDisposable? startupSub;
    // 🚨 The runner, owned until the IoPool drain (#7992). Its watches live as long as the mesh, and on a
    // silo the pools are cancelled at the silo's stop — before any host StopAsync or container disposal
    // reaches this runner — so its trigger-node watches were still attached at the cancel and were
    // reported as unowned leaks. The registry runs Dispose() before any pool is cancelled.
    private IDisposable? ownedUntilIoDrain;

    /// <summary>Test seam: opens the runner's live workspace queries (id, query). Null in production,
    /// where each opens the workspace's synced query as system.</summary>
    internal Func<string, string, IObservable<IEnumerable<MeshNode>>>? OpenQueryOverride { get; set; }

    /// <summary>Test seam: how a faulted standing query's re-open is scheduled. Null in production,
    /// where the hub watcher's own cadence applies.</summary>
    internal Func<Action, IDisposable>? ScheduleReopen { get; set; }

    /// <summary>Test seam: the authoritative read of an exact-path subscription's TARGET path. Null in
    /// production, where it is <c>GetMeshNodeOutcome</c> as system.</summary>
    internal Func<string, IObservable<NodeReadOutcome>>? ReadPathOverride { get; set; }

    /// <summary>Test seam: opens the deletion catch-up listing (purpose <see cref="CatchUpPurpose"/>) and the
    /// live deletion watch (<see cref="WatchPurpose"/>) as (purpose, query). Null in production, where each
    /// is <c>IMeshService.Query</c> as system.</summary>
    internal Func<string, string, IObservable<QueryResultChange<MeshNode>>>? OpenMeshQueryOverride { get; set; }

    internal const string CatchUpPurpose = "deletion-catch-up";
    internal const string WatchPurpose = "deletion-watch";

    /// <summary>The outstanding subscriptions as the live query last listed them.</summary>
    internal IReadOnlyList<EventSubscription> PendingSnapshot
    {
        get { lock (gate) return pending; }
    }

    // 🚨 The runner's two live queries are STANDING watches: one fault (a provider stall, a dropped
    // connection) used to end each for the life of the process — no pending subscription fired again
    // until a restart. The hub watcher re-opens a faulted query; the synced-query cache has already
    // evicted the faulted chain, so the re-open reads fresh.
    private IObservable<IEnumerable<MeshNode>> OpenQuery(string id, string query)
        => OpenQueryOverride?.Invoke(id, query) ?? AsSystem(() => hub.GetWorkspace().GetQuery(id, query));

    private IObservable<QueryResultChange<MeshNode>> OpenMeshQuery(string purpose, string query)
        => AsSystem(() => OpenMeshQueryOverride?.Invoke(purpose, query)
                          ?? meshService.Query<MeshNode>(MeshQueryRequest.FromQuery(query)));

    private IObservable<NodeReadOutcome> ReadPath(string path)
        => AsSystem(() => ReadPathOverride?.Invoke(path)
                          ?? hub.GetMeshNodeOutcome(path, TimeSpan.FromSeconds(10), ReadTimeoutBehavior.EmitNull));

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        ownedUntilIoDrain = hub.OwnUntilIoDrain(Disposable.Create(Dispose), nameof(EventSubscriptionRunner));

        // Fold any legacy ScheduledAction nodes into EventSubscription nodes so nothing in-flight is
        // lost when this runner replaces ScheduledActionRunner.
        MigrateLegacyScheduledActions();

        // Live snapshot of outstanding subscriptions; re-emits on add / fire / cancel. Reading the Admin
        // partition needs an identity → system. (Constant query id: one registry entry, no leak.)
        pendingSub = ActivityControlPlaneExtensions.SubscribeHubWatcher(
            hub,
            () => OpenQuery("event-subscriptions",
                $"path:{EventSubscriptionNodeType.Namespace} scope:children nodeType:{EventSubscriptionNodeType.NodeType} select:path,id,namespace,name,nodeType,content"),
            nodes =>
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
                foreach (var s in list.Where(s => IsTypeTrigger(s) && s.TriggerKind == MeshChangeKind.Created))
                    Reconcile(s);

                // Exact-path Deleted subscriptions: each is checked against current state ONCE — the
                // first time this runner sees it — so a delete that happened before the subscription
                // was written, or while the runner was down, still fires.
                var pathDeletions = list.Where(s => IsPathDeletion(s) && DeletionScope(s) is not null).ToList();
                ReconcileDeletedPaths(pathDeletions.Where(s => deletionChecked.TryAdd(s.Id, 0)).ToList());
                var neededScopes = pathDeletions
                    .Select(s => DeletionScope(s)!)
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var scope in neededScopes)
                    WatchDeletions(scope);
                foreach (var scope in deletionWatchSubs.Keys.Where(q => !neededScopes.Contains(q)).ToList())
                    if (deletionWatchSubs.TryRemove(scope, out var dw))
                        dw.Dispose();
                // A TriggerPath this runner cannot honour — another kind, no path left once normalised,
                // or a path the query language cannot write — is failed, named, rather than left to fall
                // into the type paths as a type trigger with no type.
                foreach (var s in list)
                    if (PathRefusal(s) is { } reason)
                        Refuse(s, reason);

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
                    .Where(s => IsTypeTrigger(s) && s is { TriggerKind: MeshChangeKind.Created, TriggerNodeType.Length: > 0 })
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
                foreach (var id in deletionChecked.Keys.Where(id => !pendingIds.Contains(id)).ToList())
                    deletionChecked.TryRemove(id, out _);
                // A confirm slot that FIRED is held until here, so no second proposal reads again while
                // the Fired write round-trips. A slot still READING is left alone: it settles on its own
                // (and the cold-start reconcile proposes before this list is warm).
                foreach (var (id, slot) in confirmSubs.Where(kv => kv.Value.Fired && !pendingIds.Contains(kv.Key)).ToList())
                    if (confirmSubs.TryRemove(new KeyValuePair<string, ConfirmSlot>(id, slot)))
                        slot.Dispose();
            },
            logger,
            "Event-subscriptions query",
            scheduleReEstablish: ScheduleReopen);

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
                var outstanding = (items ?? [])
                    .Select(ReadSubscription)
                    .Where(s => s is { Status: EventSubscriptionStatus.Pending })
                    .Select(s => s!)
                    .ToList();
                foreach (var s in outstanding.Where(s => IsTypeTrigger(s) && s.TriggerKind == MeshChangeKind.Created))
                    Reconcile(s);
                // The restart-between-delete-and-delivery case: the delete event was published while
                // nothing was listening, so the only evidence left is that the path is gone now.
                ReconcileDeletedPaths(outstanding
                    .Where(s => IsPathDeletion(s) && DeletionScope(s) is not null)
                    .Where(s => deletionChecked.TryAdd(s.Id, 0))
                    .ToList());
            }, ex => logger?.LogWarning(ex, "Startup authoritative reconcile failed"));

        // Live: fire on the actual change event.
        feedSub = changeFeed.Subscribe(OnChange);
        return Task.CompletedTask;
    }

    private void OnChange(MeshChangeEvent e)
    {
        if (e.Kind == MeshChangeKind.Deleted && NormalizePath(e.Path) is { Length: > 0 } deletedPath)
        {
            // Exact-path Deleted subscriptions match on the path alone — the event carries no
            // authoritative type and the node cannot be read back. The fire is still gated on an
            // authoritative "it is gone" read (ConfirmGoneAndFire): the path may already be back.
            List<EventSubscription> byPath;
            lock (gate)
                byPath = pending
                    .Where(s => IsPathDeletion(s) && string.Equals(ExactPath(s), deletedPath, StringComparison.Ordinal))
                    .ToList();
            foreach (var s in byPath)
                ConfirmGoneAndFire(s);
        }

        List<EventSubscription> candidates;
        lock (gate)
            candidates = pending
                .Where(s => IsTypeTrigger(s)
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
                            .Where(s => IsTypeTrigger(s) && s.TriggerKind == MeshChangeKind.Created
                                        && string.Equals(s.TriggerNodeType, nodeType, StringComparison.Ordinal))
                            .ToList();
                    foreach (var s in candidates)
                    {
                        var node = (items ?? []).FirstOrDefault(n => Matches(s, n));
                        if (node is not null)
                            Execute(s, node);
                    }
                },
                ex =>
                {
                    // 🚨 DROP THE REGISTRY ENTRY, or this node type is never watched again for the
                    // life of the runner: the `ContainsKey` guard at the top of this method is what
                    // makes the watch one-per-type, and a terminated subscription left in the
                    // dictionary short-circuits every later attempt to re-establish it. The query
                    // fan-in now TERMINATES on a stalled provider instead of parking, so this arm
                    // is reachable by an ordinary availability blip — and what it silently strands
                    // is the deferred invite/grant reconcile (a user onboards and gets no access).
                    // The same discipline as MeshNodeStreamCache.EvictFaultedQuery: nothing
                    // re-subscribes on its own, the NEXT emission that needs this watch rebuilds it.
                    // PAIR-EXACT on (nodeType, this slot), for the reason EvictFaultedQuery is: the
                    // prune at ReconcileWatches can drop this type and a later emission re-establish
                    // it, and a late terminal from the OLD chain must never evict the healthy new
                    // one. Our own slot is disposed either way — it is already dead.
                    nodeChangeSubs.TryRemove(
                        new KeyValuePair<string, IDisposable>(nodeType, slot));
                    slot.Dispose();
                    logger?.LogWarning(ex,
                        "Trigger-node watch for {NodeType} failed — dropped from the watch registry, "
                        + "so the next emission that needs it opens a fresh one", nodeType);
                });
    }

    /// <summary>The subscription's <see cref="EventSubscription.TriggerPath"/>, normalised (trimmed, no
    /// leading or trailing <c>/</c>), or null when it names none.</summary>
    private static string? ExactPath(EventSubscription subscription)
        => NormalizePath(subscription.TriggerPath) is { Length: > 0 } path ? path : null;

    private static string? NormalizePath(string? path) => path?.Trim().Trim('/');

    private static string ParentOf(string path)
        => path.LastIndexOf('/') is var i and > 0 ? path[..i] : "";

    /// <summary>
    /// The listing that covers an exact-path subscription's target: its parent's children, or — for a
    /// top-level path, which has no parent to list — the path itself (an anchored exact-scope query).
    /// The path is written as a quoted literal (<see cref="QueryParser.TryPathLiteral"/>), so a space or
    /// query syntax inside it is data. Null when the subscription names no path, or one the query
    /// language cannot write (<see cref="PathRefusal"/> fails it).
    /// </summary>
    private static string? DeletionScope(EventSubscription subscription)
    {
        if (ExactPath(subscription) is not { } path)
            return null;
        var parent = ParentOf(path);
        return parent.Length > 0
            ? QueryParser.TryPathLiteral(parent, out var parentLiteral) ? $"path:{parentLiteral} scope:children" : null
            : QueryParser.TryPathLiteral(path, out var pathLiteral) ? $"path:{pathLiteral} scope:exact" : null;
    }

    /// <summary>Why a NodeChange subscription's <see cref="EventSubscription.TriggerPath"/> cannot be
    /// honoured, or null when it names none or can be.</summary>
    private static string? PathRefusal(EventSubscription s)
    {
        if (s.TriggerType != EventTriggerType.NodeChange || s.TriggerPath is null)
            return null;
        if (ExactPath(s) is null)
            return $"Event subscription {s.Id} names TriggerPath '{s.TriggerPath}', which is not a node path "
                   + "once trimmed of whitespace and '/'.";
        if (s.TriggerKind != MeshChangeKind.Deleted)
            return $"Event subscription {s.Id} names TriggerPath '{s.TriggerPath}' with TriggerKind "
                   + $"{s.TriggerKind}; an exact-path trigger is honoured for {MeshChangeKind.Deleted} only.";
        if (DeletionScope(s) is null)
            return $"Event subscription {s.Id} names TriggerPath '{s.TriggerPath}', which the query language "
                   + "cannot write as one literal, so its deletion cannot be watched.";
        return null;
    }

    private static string IdOf(string path)
        => path.LastIndexOf('/') is var i and >= 0 ? path[(i + 1)..] : path;

    /// <summary>A NodeChange subscription matched by node TYPE (the original shape) — every type-based
    /// firing path draws only these, so a subscription that names ANY <see cref="EventSubscription.TriggerPath"/>
    /// (an exact-path one, or one <see cref="PathRefusal"/> fails) never reaches them.</summary>
    private static bool IsTypeTrigger(EventSubscription subscription)
        => subscription.TriggerType == EventTriggerType.NodeChange && subscription.TriggerPath is null;

    /// <summary>A NodeChange subscription on the <see cref="MeshChangeKind.Deleted"/> of ONE exact path.</summary>
    private static bool IsPathDeletion(EventSubscription subscription)
        => subscription is { TriggerType: EventTriggerType.NodeChange, TriggerKind: MeshChangeKind.Deleted }
           && ExactPath(subscription) is not null;

    /// <summary>
    /// Checks exact-path Deleted subscriptions against CURRENT state and fires each whose path is gone —
    /// the catch-up for a delete nobody heard (it happened while the runner was down, or before the
    /// subscription was written). One listing per deletion scope (<see cref="DeletionScope"/>) narrows the
    /// candidates cheaply; a path the listing still shows is left alone, and every path it does NOT show
    /// is confirmed by an authoritative read before anything fires (<see cref="ConfirmGoneAndFire"/>),
    /// because a listing can trail a node that was written a moment ago. A listing that fails releases
    /// its subscriptions' checks so a later emission retries them. Each listing is owned by the runner
    /// (<see cref="catchUpSubs"/>), so a stop disposes it.
    /// </summary>
    private void ReconcileDeletedPaths(IReadOnlyList<EventSubscription> subscriptions)
    {
        foreach (var group in subscriptions.GroupBy(s => DeletionScope(s)!, StringComparer.Ordinal))
        {
            if (disposed)
                return;
            var members = group.ToList();
            var slot = new SingleAssignmentDisposable();
            catchUpSubs.Add(slot);
            slot.Disposable = OpenMeshQuery(CatchUpPurpose, group.Key)
                .Where(c => c.ChangeType == QueryChangeType.Initial)
                .Select(c => c.Items)
                .Take(1)
                // Completed, faulted or disposed: drop it from the owned set (a no-op after Dispose).
                .Finally(() => catchUpSubs.Remove(slot))
                .Subscribe(items =>
                {
                    if (disposed)
                        return;
                    var listed = (items ?? [])
                        .Select(n => NormalizePath(n.Path))
                        .Where(p => p is { Length: > 0 })
                        .ToHashSet(StringComparer.Ordinal);
                    foreach (var s in members.Where(s => !listed.Contains(ExactPath(s)!)))
                        ConfirmGoneAndFire(s);
                }, ex =>
                {
                    foreach (var s in members)
                        deletionChecked.TryRemove(s.Id, out _);
                    logger?.LogWarning(ex,
                        "Listing {Scope} to reconcile {Count} exact-path deletion subscription(s) failed — "
                        + "released, so a later emission retries", group.Key, members.Count);
                });
        }
    }

    /// <summary>
    /// Fires an exact-path Deleted subscription only once an AUTHORITATIVE read of its path says the node
    /// is gone: <see cref="NodeReadStatus.Absent"/> with no <see cref="NodeReadOutcome.HiddenBy"/> verdict,
    /// or <see cref="NodeReadStatus.DeleteInProgress"/> (directional — its next answer is "gone", never
    /// "here again"). A path that reads <see cref="NodeReadStatus.Present"/> never fires — it was
    /// re-created, or the hint that sent us here was stale. A node a read validator HID is no evidence of
    /// a delete — the owner runs read validators only over a node it still holds — and neither is
    /// <see cref="NodeReadStatus.Unavailable"/>: nothing fires, and the check is released so a later
    /// pending-set emission (or the next startup) retries.
    ///
    /// <para>The whole confirm-and-fire is reserved per subscription (<see cref="confirmSubs"/>) before the
    /// read, owned by the runner, and re-reads the SUBSCRIPTION before firing — it must still be Pending,
    /// the rule <see cref="ScheduleTimer"/> applies at fire time — so a proposal that raced the terminal
    /// write, or a reply that lands after the runner stopped, delivers nothing.</para>
    /// </summary>
    private void ConfirmGoneAndFire(EventSubscription subscription)
    {
        if (disposed)
            return;
        var slot = new ConfirmSlot();
        if (!confirmSubs.TryAdd(subscription.Id, slot))
            return;   // another proposal owns this subscription's confirm-and-fire
        var path = ExactPath(subscription)!;
        slot.Read.Disposable = ReadPath(path)
            .Take(1)
            .SelectMany(outcome => outcome is { Status: NodeReadStatus.Absent, HiddenBy: null }
                                   or { Status: NodeReadStatus.DeleteInProgress }
                ? ReadSubscriptionNow(subscription.Id).Select(live => (outcome, live))
                : Observable.Return((outcome, live: (EventSubscription?)null)))
            .Subscribe(result =>
            {
                if (disposed)
                    return;
                var (outcome, live) = result;
                switch (outcome)
                {
                    case { Status: NodeReadStatus.Absent, HiddenBy: null }:
                    case { Status: NodeReadStatus.DeleteInProgress }:
                        if (live is not { Status: EventSubscriptionStatus.Pending })
                        {
                            // Fired, Cancelled or Failed meanwhile — or unreadable: nothing to deliver here.
                            Release(subscription.Id, slot, recheck: live is null);
                            logger?.LogDebug(
                                "Exact-path deletion subscription {Id}: {Path} is gone but the subscription is {Status} — not firing",
                                subscription.Id, path, live?.Status.ToString() ?? "unreadable");
                            return;
                        }
                        slot.Fired = true;
                        Execute(live, IdOf(path));
                        break;
                    case { Status: NodeReadStatus.Present }:
                        Release(subscription.Id, slot, recheck: false);
                        logger?.LogDebug(
                            "Exact-path deletion subscription {Id}: {Path} still exists — not firing",
                            subscription.Id, path);
                        break;
                    case { Status: NodeReadStatus.Absent }:
                        Release(subscription.Id, slot, recheck: true);
                        logger?.LogWarning(
                            "Exact-path deletion subscription {Id}: {Path} exists but a read validator hid it ({Verdict}) — "
                            + "not firing; released so a later emission retries", subscription.Id, path, outcome.HiddenBy);
                        break;
                    default:
                        Release(subscription.Id, slot, recheck: true);
                        logger?.LogWarning(outcome.Failure,
                            "Exact-path deletion subscription {Id}: could not establish whether {Path} is gone — "
                            + "not firing; released so a later emission retries", subscription.Id, path);
                        break;
                }
            }, ex =>
            {
                Release(subscription.Id, slot, recheck: true);
                logger?.LogWarning(ex,
                    "Exact-path deletion subscription {Id}: reading {Path} failed — not firing; released so a "
                    + "later emission retries", subscription.Id, path);
            });
    }

    /// <summary>Frees a confirm slot that settled without firing, pair-exact (a later proposal may already
    /// hold a fresh one); <paramref name="recheck"/> also releases the subscription's catch-up check.</summary>
    private void Release(string id, ConfirmSlot slot, bool recheck)
    {
        if (confirmSubs.TryRemove(new KeyValuePair<string, ConfirmSlot>(id, slot)))
            slot.Dispose();
        if (recheck)
            deletionChecked.TryRemove(id, out _);
    }

    /// <summary>The subscription as its owner holds it NOW (null when it cannot be read) — the fire-time
    /// re-read.</summary>
    private IObservable<EventSubscription?> ReadSubscriptionNow(string id)
        => AsSystem(() => hub.GetMeshNodeOutcome(
                EventSubscriptionNodeType.Path(id), TimeSpan.FromSeconds(10), ReadTimeoutBehavior.EmitNull))
            .Take(1)
            .Select(o => o.Status == NodeReadStatus.Present ? ReadSubscription(o.Node!) : null);

    /// <summary>
    /// A LIVE listing of one deletion scope (<see cref="DeletionScope"/> — a parent's children, or a
    /// top-level path itself) that proposes every pending exact-path Deleted subscription in that scope
    /// to <see cref="ConfirmGoneAndFire"/> when the listing says its path is gone: a <c>Removed</c> delta
    /// naming it, or an <c>Initial</c>/<c>Reset</c> snapshot that no longer holds it (a delete between the
    /// catch-up and this watch opening, or while a faulted watch was being re-opened, emits no Removed).
    /// The change-feed-INDEPENDENT path for a delete, the twin of <see cref="WatchTriggerNodeType"/>: a
    /// synced query re-emits through the storage layer, so a delete committed on another silo still
    /// reaches this runner when the change feed's best-effort relay drops it. One watch per scope,
    /// disposed when no pending subscription needs it, dropped from the registry on a terminal fault so
    /// the next emission re-opens it.
    /// </summary>
    private void WatchDeletions(string scope)
    {
        if (deletionWatchSubs.ContainsKey(scope))
            return;
        var slot = new SingleAssignmentDisposable();
        if (!deletionWatchSubs.TryAdd(scope, slot))
            return;
        slot.Disposable = OpenMeshQuery(WatchPurpose, scope)
            // Removed: its Items are the deleted nodes. Initial/Reset: the full snapshot, in which a
            // pending path that is MISSING is gone. Added/Updated say only that something exists.
            .Where(c => c.ChangeType is QueryChangeType.Removed or QueryChangeType.Initial or QueryChangeType.Reset)
            .Subscribe(
                change =>
                {
                    if (disposed)
                        return;
                    var paths = (change.Items ?? [])
                        .Select(n => NormalizePath(n.Path))
                        .Where(p => p is { Length: > 0 })
                        .ToHashSet(StringComparer.Ordinal);
                    var removed = change.ChangeType == QueryChangeType.Removed;
                    List<EventSubscription> candidates;
                    lock (gate)
                        candidates = pending
                            .Where(s => IsPathDeletion(s)
                                        && string.Equals(DeletionScope(s), scope, StringComparison.Ordinal)
                                        && paths.Contains(ExactPath(s)!) == removed)
                            .ToList();
                    foreach (var s in candidates)
                        ConfirmGoneAndFire(s);
                },
                ex =>
                {
                    // Pair-exact, for the reason WatchTriggerNodeType's terminal arm is.
                    deletionWatchSubs.TryRemove(new KeyValuePair<string, IDisposable>(scope, slot));
                    slot.Dispose();
                    logger?.LogWarning(ex,
                        "Deletion watch {Scope} failed — dropped from the watch registry, so the next "
                        + "emission that needs it opens a fresh one", scope);
                });
    }

    /// <summary>Ends a subscription this runner cannot honour as <c>Failed</c>, with
    /// <paramref name="reason"/> on <see cref="EventSubscription.LastError"/>. Reserved like a firing, so
    /// repeated pending-set emissions issue one write.</summary>
    private void Refuse(EventSubscription subscription, string reason)
    {
        if (!executing.TryAdd(subscription.Id, 0))
            return;
        logger?.LogWarning("Event subscription {Id} refused: {Reason}", subscription.Id, reason);
        AsSystem(() => EventSubscriptionOps.SetStatus(
                hub, EventSubscriptionNodeType.Path(subscription.Id), EventSubscriptionStatus.Failed, reason))
            .Subscribe(_ => { }, ex =>
            {
                executing.TryRemove(subscription.Id, out _);
                logger?.LogWarning(ex, "Recording the refusal of event subscription {Id} failed", subscription.Id);
            });
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
        if (disposed || !executing.TryAdd(subscription.Id, 0))
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
            //
            // 🚨 Issued on ReadIssuingHub(), never on `hub` itself: this runner is an IHostedService
            // resolved from the mesh's root container, so `hub` IS the router, and observing on it
            // made the router both the sender of the request and the target of the script's reply
            // — the one-shot request/response shape of #5937/#5620. The script executes on the
            // TARGET; this side is only the reply mailbox, which is what the read seam is for.
            EventContinuationType.RunScript when subscription is { TargetPath.Length: > 0 } =>
                AsSystem(() => hub.ReadIssuingHub().Observe<ExecuteScriptResponse>(
                        new ExecuteScriptRequest(),
                        o => o.WithTarget(new Address(subscription.TargetPath!)))
                    .Take(1)
                    .Timeout(TimeSpan.FromMinutes(30))
                    .Select(_ => new MeshNode(subscription.TargetPath!))),

            // The generic module continuation: the firing is written onto the module's own node, whose
            // hub decides what it means. In-mesh source owns a continuation this way.
            EventContinuationType.DeliverToNode when !string.IsNullOrWhiteSpace(subscription.DeliverTo) =>
                Deliver(subscription, userId),
            EventContinuationType.DeliverToNode =>
                Unhandled(subscription, $"Event subscription {subscription.Id} has continuation "
                    + $"{subscription.ContinuationType} but names no DeliverTo node — there is nobody to deliver it to."),

            // Continuations this assembly cannot implement — their effects live ABOVE it in the
            // reference graph (PublishSocialPost is owned by MeshWeaver.Social, which references
            // Graph). The owning module registers an IEventContinuationHandler. A value nobody claims
            // (a number from a newer peer, say) is never treated as one this runner knows: it fails
            // naming itself.
            _ => ExternalContinuation(subscription, userId),
        };

    /// <summary>
    /// Hands the firing to the node named by <see cref="EventSubscription.DeliverTo"/>: an
    /// <see cref="EventDelivery"/> written onto its content, as System, through its own stream. The
    /// subscription is <c>Fired</c> once that write lands; what the module then does is reported on
    /// its own node, which is where its owner looks.
    /// </summary>
    private IObservable<MeshNode> Deliver(EventSubscription subscription, string subjectId)
    {
        var delivery = new EventDelivery
        {
            SubscriptionId = subscription.Id,
            SubjectId = subjectId,
            FiredAt = DateTimeOffset.UtcNow,
            CreatedBy = subscription.CreatedBy,
        };
        return AsSystem(() => EventSubscriptionOps.Deliver(hub, subscription.DeliverTo!.Trim(), delivery));
    }

    /// <summary>Logs a continuation this runner cannot carry out, by name, and fails the subscription
    /// with <paramref name="reason"/> (recorded on <see cref="EventSubscription.LastError"/> by the
    /// caller's Failed write).</summary>
    private IObservable<MeshNode> Unhandled(EventSubscription subscription, string reason)
    {
        logger?.LogWarning(
            "Event subscription {Id} with continuation {Continuation} cannot be carried out: {Reason}",
            subscription.Id, subscription.ContinuationType, reason);
        return Observable.Throw<MeshNode>(new InvalidOperationException(reason));
    }

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
    ///   <c>AddSocial</c>, say), or the value is one this build does not know at all. The generic
    ///   "unsupported subscription" wording sent readers looking for a bad subscription instead of an
    ///   absent registration.</item>
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

        return Unhandled(subscription, handlers.Count == 0
            ? $"Event subscription {subscription.Id} has continuation "
              + $"{subscription.ContinuationType}, which no IEventContinuationHandler is registered "
              + "for. The module that owns it is not composed into this host — nothing will ever fire "
              + "this subscription until it is."
            : $"Event subscription {subscription.Id} has continuation "
              + $"{subscription.ContinuationType}, which {handlers.Count} IEventContinuationHandlers "
              + $"claim ({string.Join(", ", handlers.Select(h => h.GetType().Name))}). Exactly one must "
              + "be registered — refusing to pick one, because which arrived first is not a decision "
              + "this runner can make correctly.");
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
        legacySub = ActivityControlPlaneExtensions.SubscribeHubWatcher(
            hub,
            () => OpenQuery("event-subscriptions-legacy",
                $"path:{ScheduledActionNodeType.Namespace} scope:children nodeType:{ScheduledActionNodeType.NodeType} select:path,id,namespace,name,nodeType,content"),
            nodes =>
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
            },
            logger,
            "Legacy ScheduledAction migration query",
            scheduleReEstablish: ScheduleReopen);
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
        disposed = true;
        // Ends the ownership (a stop before the drain); a no-op when the drain's release is the caller.
        Interlocked.Exchange(ref ownedUntilIoDrain, null)?.Dispose();
        pendingSub?.Dispose();
        feedSub?.Dispose();
        legacySub?.Dispose();
        startupSub?.Dispose();
        foreach (var subs in new[] { timerSubs, statusSubs, nodeChangeSubs, deletionWatchSubs })
            foreach (var id in subs.Keys.ToList())
                if (subs.TryRemove(id, out var d))
                    d.Dispose();
        foreach (var id in confirmSubs.Keys.ToList())
            if (confirmSubs.TryRemove(id, out var confirm))
                confirm.Dispose();
        catchUpSubs.Dispose();
        executing.Clear();
        deletionChecked.Clear();
    }
}
