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
/// Executes deferred <see cref="ScheduledAction"/>s. Two paths keep it resilient across restarts:
/// <list type="bullet">
///   <item><b>Live</b> — subscribes to the mesh change feed; when a node of a pending action's
///     <see cref="ScheduledAction.TriggerNodeType"/> is changed and its field matches, the action fires.</item>
///   <item><b>Reconcile</b> — a live query over the outstanding actions re-evaluates each on startup
///     (and whenever the set changes) against CURRENT state, so a trigger that happened while the
///     runner was down — e.g. the invitee signed up during a deploy — still fires. The durable state
///     is the action node itself (Pending → Fired), so nothing is lost.</item>
/// </list>
/// Effects are idempotent (create-or-update grant, pin is a set-add, the terminal <c>Fired</c> write
/// gates re-entry), so the two paths can't double-grant.
///
/// <para>🚨 The runner has NO ambient <c>AccessContext</c> (it's a background hosted service, not a
/// request). Every read AND write goes through <see cref="AsSystem{T}"/> — <c>Using(ImpersonateAsSystem,
/// Defer(factory))</c> — so the operation is both CONSTRUCTED and subscribed under the system identity.
/// Both matter: the write primitives capture the caller identity when constructed (so a grant built
/// outside the scope would capture nothing and fail closed), and reads capture it at subscribe. Deferring
/// construction into the <c>Using</c> factory covers the <c>SelectMany</c> inners too, which would
/// otherwise be constructed on a downstream emission thread with no system context.</para>
///
/// <para>Reconciliation is exact for <see cref="MeshChangeKind.Created"/> triggers (the entity persists).
/// A durable event-log + cursor (a separate PG queue) is the follow-up that would also give exact replay
/// for Update/Delete triggers and a general event stream.</para>
/// </summary>
public sealed class ScheduledActionRunner(
    IMessageHub hub,
    IMeshChangeFeed changeFeed,
    IMeshService meshService,
    AccessService accessService,
    ILogger<ScheduledActionRunner>? logger = null) : IHostedService, IDisposable
{
    private readonly object gate = new();
    private IReadOnlyList<ScheduledAction> pending = [];
    private IDisposable? pendingSub;
    private IDisposable? feedSub;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Live snapshot of outstanding actions; re-emits on add / fire / cancel. Reading the Admin
        // partition needs an identity → system. (Constant query id: one registry entry, no leak.)
        pendingSub = AsSystem(() => hub.GetWorkspace().GetQuery("scheduled-actions",
                $"path:{ScheduledActionNodeType.Namespace} scope:children nodeType:{ScheduledActionNodeType.NodeType}"))
            .Subscribe(nodes =>
            {
                var list = (nodes ?? [])
                    .Select(n => n.Content as ScheduledAction)
                    .Where(a => a is { Status: ScheduledActionStatus.Pending })
                    .Select(a => a!)
                    .ToList();
                lock (gate) pending = list;

                // Reconcile Created-trigger actions against current state (catch missed triggers).
                foreach (var action in list.Where(a => a.TriggerKind == MeshChangeKind.Created))
                    Reconcile(action);
            }, ex => logger?.LogWarning(ex, "Scheduled-actions query failed"));

        // Live: fire on the actual change event.
        feedSub = changeFeed.Subscribe(OnChange);
        return Task.CompletedTask;
    }

    private void OnChange(MeshChangeEvent e)
    {
        List<ScheduledAction> candidates;
        lock (gate)
            candidates = pending
                .Where(a => a.TriggerKind == e.Kind
                            && string.Equals(a.TriggerNodeType, e.NodeType, StringComparison.Ordinal))
                .ToList();
        if (candidates.Count == 0)
            return;

        // Read the triggering node once (system identity); evaluate each candidate's field match.
        AsSystem(() => hub.GetMeshNode(e.Path, TimeSpan.FromSeconds(10))).Subscribe(node =>
        {
            if (node is null)
                return;
            foreach (var action in candidates)
                if (Matches(action, node))
                    Execute(action, node);
        }, ex => logger?.LogWarning(ex, "Reading triggering node {Path} failed", e.Path));
    }

    private void Reconcile(ScheduledAction action)
    {
        var query = $"nodeType:{action.TriggerNodeType}";
        if (action is { MatchField.Length: > 0, MatchValue.Length: > 0 })
            query += $" content.{action.MatchField}:{action.MatchValue}";

        // One-shot lookup via Query<T> (NOT the workspace GetQuery, which caches by id for the
        // workspace lifetime — a per-action id would leak a registry entry each time). Take the
        // initial snapshot and stop.
        AsSystem(() => meshService.Query<MeshNode>(MeshQueryRequest.FromQuery(query))
                .Where(c => c.ChangeType == QueryChangeType.Initial)
                .Select(c => c.Items)
                .Take(1))
            .Subscribe(items =>
            {
                var node = items.FirstOrDefault(n => Matches(action, n));
                if (node is not null)
                    Execute(action, node);
            }, ex => logger?.LogWarning(ex, "Reconcile query for action {Id} failed", action.Id));
    }

    private bool Matches(ScheduledAction action, MeshNode node)
    {
        if (!string.Equals(action.TriggerNodeType, node.NodeType, StringComparison.Ordinal))
            return false;
        if (action.MatchField is not { Length: > 0 } field)
            return true;
        var actual = ScheduledActionOps.ReadContentField(node, field, hub.JsonSerializerOptions);
        return string.Equals(actual, action.MatchValue, StringComparison.OrdinalIgnoreCase);
    }

    private void Execute(ScheduledAction action, MeshNode triggerNode)
    {
        // The triggering node's id is the user (a User node's path IS the userId).
        var userId = triggerNode.Id;

        BuildEffect(action, userId)
            .SelectMany(_ => AsSystem(() => ScheduledActionOps.SetStatus(hub, action.Id, ScheduledActionStatus.Fired)))
            .Subscribe(
                _ => logger?.LogInformation(
                    "Scheduled action {Id} fired: {Kind} {Role} for {User} on {Target}",
                    action.Id, action.ActionKind, action.Role, userId, action.TargetPath),
                ex =>
                {
                    logger?.LogWarning(ex, "Scheduled action {Id} failed", action.Id);
                    AsSystem(() => ScheduledActionOps.SetStatus(hub, action.Id, ScheduledActionStatus.Failed, ex.Message))
                        .Subscribe(_ => { }, _ => { });
                });
    }

    private IObservable<MeshNode> BuildEffect(ScheduledAction action, string userId) => action.ActionKind switch
    {
        ScheduledActionKind.GrantSpaceAccess when action is { TargetPath.Length: > 0, Role.Length: > 0 } =>
            AsSystem(() => ScheduledActionOps.Grant(meshService, userId, action.TargetPath!, action.Role!))
                .SelectMany(g => action.Pin
                    ? AsSystem(() => ScheduledActionOps.Pin(hub, userId, action.TargetPath!)).Select(_ => g)
                    : Observable.Return(g)),
        _ => Observable.Throw<MeshNode>(new InvalidOperationException(
            $"Unsupported or incomplete scheduled action {action.Id}")),
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
    }
}
