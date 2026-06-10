using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// Enforces the "a partition must always keep at least one administrator" invariant
/// for Spaces (and any partition's <c>_Access</c> subtree): an
/// <see cref="AccessAssignment"/> that currently grants the non-denied <c>Admin</c> role
/// cannot be <b>deleted</b> — nor <b>demoted/denied</b> via update — when it is the last
/// such admin on its partition.
///
/// <para>This is the access-control half of the spec restored after the Organization→Space
/// migration: anyone may create a Space and becomes its Admin
/// (<see cref="SpaceNodeType"/>'s post-creation handler), and a Space must never be left
/// without an admin who can manage it. A global admin (root <c>_Access</c> Admin) can always
/// re-grant, so the invariant only guards the per-partition assignments.</para>
///
/// <para>Scope: assignments at <c>{partition}/_Access/...</c> only. Root-scope global-admin
/// assignments (<c>_Access/{user}_Access</c>, no partition prefix) are exempt.</para>
///
/// <para><b>Deadlock-safe read.</b> The remaining-admin count comes from
/// <see cref="IMeshService.Query{T}"/> (the read-side query provider — a direct
/// store/DB read), NOT a <c>workspace.GetQuery</c> synced subscription: the validator runs
/// inside the delete/update pipeline on the owning partition hub, and a synced query that
/// round-tripped back to that same hub would deadlock (see
/// <c>feedback_synced_query_thread_hub</c>). The read-side query is eventually consistent,
/// which is acceptable here — this is a guard-rail, not the security boundary (RLS already
/// gates who may write to <c>_Access</c>).</para>
/// </summary>
public sealed class SpaceAdminInvariantValidator(IMessageHub hub, ILogger<SpaceAdminInvariantValidator>? logger = null)
    : INodeValidator
{
    private const string AccessAssignmentNodeType = "AccessAssignment";
    private const string AccessSegment = "/_Access";

    public IReadOnlyCollection<NodeOperation> SupportedOperations =>
        [NodeOperation.Delete, NodeOperation.Update];

    public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
    {
        var node = context.Node;
        if (!string.Equals(node.NodeType, AccessAssignmentNodeType, StringComparison.OrdinalIgnoreCase))
            return Observable.Return(NodeValidationResult.Valid());

        // Only guard assignments under a partition's _Access subtree:
        // path == "{partition}/_Access/{id}". Root-scope assignments ("_Access/{id}",
        // no leading partition) have no "/_Access" segment and are exempt.
        var path = node.Path;
        var idx = path?.IndexOf(AccessSegment, StringComparison.Ordinal) ?? -1;
        if (path is null || idx <= 0)
            return Observable.Return(NodeValidationResult.Valid());
        var partition = path[..idx];

        // The whole partition is going away: when this delete is part of a cascade rooted at
        // the partition (or an ancestor of it), the space itself is being removed — keeping
        // "at least one admin" is moot, and blocking here would make deleting a Space
        // impossible (the cascade always reaches the creator's _Access/{user} admin
        // assignment). Only enforce the invariant when the assignment is removed while its
        // partition stays. A null cascade root (standalone ValidateDeleteRequest) is treated
        // as "partition stays" — the safe default that still guards direct admin removal.
        if (context.Operation == NodeOperation.Delete
            && context.DeleteCascadeRootPath is { } root
            && IsAtOrBelow(partition, root))
            return Observable.Return(NodeValidationResult.Valid());

        // Does this operation actually REMOVE an admin?
        //  • Delete: the deleted node currently grants non-denied Admin.
        //  • Update: the PREVIOUS state granted Admin and the NEW state no longer does.
        var removesAdmin = context.Operation switch
        {
            NodeOperation.Delete => GrantsAdmin(node),
            NodeOperation.Update => GrantsAdmin(context.ExistingNode) && !GrantsAdmin(node),
            _ => false,
        };
        if (!removesAdmin)
            return Observable.Return(NodeValidationResult.Valid());

        var meshService = hub.ServiceProvider.GetService<IMeshService>();
        if (meshService is null)
            return Observable.Return(NodeValidationResult.Valid());

        // Count the OTHER non-denied Admin assignments remaining in {partition}/_Access.
        return meshService
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"namespace:{partition}{AccessSegment} nodeType:{AccessAssignmentNodeType}"))
            .Where(change => change.ChangeType == QueryChangeType.Initial)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(10))
            .Select(change =>
            {
                var remaining = change.Items.Count(other =>
                    !string.Equals(other.Id, node.Id, StringComparison.OrdinalIgnoreCase)
                    && GrantsAdmin(other));

                if (remaining > 0)
                    return NodeValidationResult.Valid();

                logger?.LogInformation(
                    "Blocked last-admin removal on partition '{Partition}' (assignment '{Id}', op {Op})",
                    partition, node.Id, context.Operation);
                return NodeValidationResult.Invalid(
                    $"Cannot remove the last administrator of '{partition}'. " +
                    "A space must always have at least one admin — grant another user the Admin role first.",
                    NodeRejectionReason.ValidationFailed);
            })
            .Catch<NodeValidationResult, Exception>(ex =>
            {
                // Guard-rail, not a security boundary (RLS already gates who may write here):
                // a lookup failure/timeout falls through to Valid rather than wedging all
                // admin churn. Logged so a genuinely broken read-side is visible.
                logger?.LogWarning(ex,
                    "SpaceAdminInvariantValidator: admin-count query failed for '{Partition}' — allowing", partition);
                return Observable.Return(NodeValidationResult.Valid());
            });
    }

    /// <summary>
    /// True when <paramref name="path"/> is <paramref name="ancestor"/> itself or lives
    /// beneath it — i.e. deleting <paramref name="ancestor"/> takes <paramref name="path"/>
    /// with it. Case-insensitive to match the partition comparisons elsewhere.
    /// </summary>
    private static bool IsAtOrBelow(string path, string ancestor) =>
        string.Equals(path, ancestor, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(ancestor + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="node"/>'s <see cref="AccessAssignment"/> content grants the
    /// <c>Admin</c> role with <see cref="RoleAssignment.Denied"/> = false. Tolerates content
    /// still carried as a <see cref="JsonElement"/> (source hub without the typed registry).
    /// </summary>
    private bool GrantsAdmin(MeshNode? node)
    {
        var assignment = node?.Content switch
        {
            AccessAssignment aa => aa,
            JsonElement je => TryDeserialize(je),
            _ => null,
        };
        return assignment?.Roles is { } roles
            && roles.Any(r => !r.Denied
                && string.Equals(r.Role, Role.Admin.Id, StringComparison.OrdinalIgnoreCase));
    }

    private AccessAssignment? TryDeserialize(JsonElement je)
    {
        try { return JsonSerializer.Deserialize<AccessAssignment>(je.GetRawText(), hub.JsonSerializerOptions); }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "SpaceAdminInvariantValidator: could not deserialize AccessAssignment content");
            return null;
        }
    }
}
