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
/// write gates re-entry), so the two paths can't double-apply.
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
    private IDisposable? pendingSub;
    private IDisposable? feedSub;
    private IDisposable? legacySub;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Fold any legacy ScheduledAction nodes into EventSubscription nodes so nothing in-flight is
        // lost when this runner replaces ScheduledActionRunner.
        MigrateLegacyScheduledActions();

        // Live snapshot of outstanding subscriptions; re-emits on add / fire / cancel. Reading the Admin
        // partition needs an identity → system. (Constant query id: one registry entry, no leak.)
        pendingSub = AsSystem(() => hub.GetWorkspace().GetQuery("event-subscriptions",
                $"path:{EventSubscriptionNodeType.Namespace} scope:children nodeType:{EventSubscriptionNodeType.NodeType}"))
            .Subscribe(nodes =>
            {
                var list = (nodes ?? [])
                    .Select(n => n.Content as EventSubscription)
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
                var pendingIds = list.Select(s => s.Id).ToHashSet();
                foreach (var (subs, _) in new[] { (timerSubs, 0), (statusSubs, 0) })
                    foreach (var id in subs.Keys.Where(id => !pendingIds.Contains(id)).ToList())
                        if (subs.TryRemove(id, out var d))
                            d.Dispose();
            }, ex => logger?.LogWarning(ex, "Event-subscriptions query failed"));

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
        var query = $"nodeType:{subscription.TriggerNodeType}";
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
        BuildContinuation(subscription, userId)
            .SelectMany(_ => AsSystem(() => EventSubscriptionOps.SetStatus(
                hub, EventSubscriptionNodeType.Path(subscription.Id), EventSubscriptionStatus.Fired)))
            .Subscribe(
                _ => logger?.LogInformation(
                    "Event subscription {Id} fired: {Continuation} {Role} for {User} on {Target}",
                    subscription.Id, subscription.ContinuationType, subscription.Role, userId, subscription.TargetPath),
                ex =>
                {
                    logger?.LogWarning(ex, "Event subscription {Id} failed", subscription.Id);
                    AsSystem(() => EventSubscriptionOps.SetStatus(
                            hub, EventSubscriptionNodeType.Path(subscription.Id), EventSubscriptionStatus.Failed, ex.Message))
                        .Subscribe(_ => { }, _ => { });
                });
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
        slot.Disposable = Observable.Timer(delay).Subscribe(_tick =>
        {
            if (timerSubs.TryRemove(subscription.Id, out var d))
                d.Dispose();
            Execute(subscription, subscription.SubjectId ?? "");
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
            // A GrantSpaceAccess that failed the guard (missing TargetPath/Role/subject) is INCOMPLETE —
            // surface that, don't fall through to the handler seam (which is only for non-native types).
            EventContinuationType.GrantSpaceAccess => Observable.Throw<MeshNode>(new InvalidOperationException(
                $"Incomplete GrantSpaceAccess event subscription {subscription.Id} (needs TargetPath, Role, and a subject)")),
            // Continuations whose effect lives above Graph (e.g. PostThreadMessage in MeshWeaver.AI) run
            // through a registered IEventSubscriptionContinuationHandler resolved from DI, wrapped in
            // AsSystem so the handler's writes run under the system identity.
            _ => hub.ServiceProvider
                    .GetServices<IEventSubscriptionContinuationHandler>()
                    .FirstOrDefault(h => h.Handles == subscription.ContinuationType) is { } handler
                ? AsSystem(() => handler.Run(subscription))
                : Observable.Throw<MeshNode>(new InvalidOperationException(
                    $"No continuation handler for {subscription.ContinuationType} (subscription {subscription.Id})")),
        };

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
                $"path:{ScheduledActionNodeType.Namespace} scope:children nodeType:{ScheduledActionNodeType.NodeType}"))
            .Subscribe(nodes =>
            {
                foreach (var node in nodes ?? [])
                {
                    if (node.Content is not ScheduledAction legacy)
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
        => Observable.Using(accessService.ImpersonateAsSystem, _ => Observable.Defer(factory));

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
        foreach (var subs in new[] { timerSubs, statusSubs })
            foreach (var id in subs.Keys.ToList())
                if (subs.TryRemove(id, out var d))
                    d.Dispose();
    }
}
