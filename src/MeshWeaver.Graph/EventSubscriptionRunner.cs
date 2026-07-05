using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
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
/// <c>Admin/EventSubscription/{id}</c> so an in-flight invite is never dropped. Timer +
/// NodeStatus triggers are added in later stages; only <see cref="EventTriggerType.NodeChange"/> is
/// handled here.</para>
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

    private void Execute(EventSubscription subscription, MeshNode triggerNode)
    {
        // The triggering node's id is the user (a User node's path IS the userId).
        var userId = triggerNode.Id;

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

    private IObservable<MeshNode> BuildContinuation(EventSubscription subscription, string userId) =>
        subscription.ContinuationType switch
        {
            EventContinuationType.GrantSpaceAccess when subscription is { TargetPath.Length: > 0, Role.Length: > 0 } =>
                AsSystem(() => EventSubscriptionOps.Grant(meshService, userId, subscription.TargetPath!, subscription.Role!))
                    .SelectMany(g => subscription.Pin
                        ? AsSystem(() => EventSubscriptionOps.Pin(hub, userId, subscription.TargetPath!)).Select(_ => g)
                        : Observable.Return(g)),
            _ => Observable.Throw<MeshNode>(new InvalidOperationException(
                $"Unsupported or incomplete event subscription {subscription.Id}")),
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
    }
}
