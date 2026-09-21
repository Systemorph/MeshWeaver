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
/// <para>🚨 <b>Two kinds of partition are exempt because they have no HUMAN administrator to keep,
/// and holding one there makes their documented end state unreachable</b>: a system-managed mirror
/// (<c>WellKnownPartitions.IsMirror</c>), and a SYSTEM-OWNED partition — one with a ONE-WAY
/// <c>_GitSync</c>, whose content is rewritten from its repo on every sync so that the only writer
/// whose edits survive is the importer identity. For the second, this invariant used to meet
/// <c>AccessAssignmentGuard.IsForbiddenOnSystemOwned</c> head-on: that rule refuses GRANTING another
/// Admin there, this one refused REMOVING the existing one, so
/// <c>SystemOwnedAccessRetractionHandler</c>'s sweep could only spare the last grant on every run
/// and the partition kept a human Admin over content nobody may usefully edit (#5140). A BIJECTIVE
/// sync is deliberately NOT exempt — there the mesh nodes are somebody's working copy, editing them
/// is the point, and the people doing it must keep write access.</para>
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

    /// <summary>The node operations this validator applies to: Delete and Update.</summary>
    public IReadOnlyCollection<NodeOperation> SupportedOperations =>
        [NodeOperation.Delete, NodeOperation.Update];

    /// <summary>
    /// Validates a delete or update of an AccessAssignment, blocking the removal or
    /// demotion of the last non-denied Admin assignment on a partition's <c>_Access</c>
    /// subtree so a space is never left without an administrator.
    /// </summary>
    /// <param name="context">The validation context describing the node and operation.</param>
    /// <returns>An observable that emits the validation result for the operation.</returns>
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

        // System-managed mirror partitions (User, Auth) have no user administrator and reject
        // interactive writes; the "keep at least one admin" invariant does not apply to them. This
        // also lets an errant `Auth/_Access` admin grant (from a pre-fix partition bootstrap) be
        // removed instead of being pinned forever as "the last admin of Auth".
        if (WellKnownPartitions.IsMirror(partition))
            return Observable.Return(NodeValidationResult.Valid());

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

        // 🚨 A SYSTEM-OWNED partition has no user administrator to keep, and holding one there makes
        // the documented end state UNREACHABLE. Once a partition has a one-way `_GitSync` its
        // content is rewritten from the repo on every sync, so the only identity that may write it
        // is the importer's (AccessAssignmentGuard.IsSystemOwned). Two rules then meet head-on:
        // AccessAssignmentGuard.IsForbiddenOnSystemOwned refuses GRANTING another Admin there, and
        // this invariant refuses REMOVING the existing one — so
        // SystemOwnedAccessRetractionHandler's sweep can only spare that last grant, every time it
        // runs, and the partition keeps a human Admin over content nobody may usefully edit (whose
        // edits the next sync reverts). Exempting the invariant is what lets the sweep converge:
        // the partition does keep an administrator, and it is the System identity. The existing
        // mirror-partition exemption above is the same reasoning one step earlier.
        //
        // The read is the shared authoritative one (storage, then static/config providers) rather
        // than a stream subscription — this runs inside the delete/update pipeline ON the owning
        // partition hub, where a synced read that round-trips back to that hub deadlocks. It is
        // paid only once this operation is already known to remove an admin, so an ordinary
        // _Access write costs nothing.
        return NodeTypeAccessRuleGate
            .ReadSubjectNode(hub, AccessAssignmentGuard.SyncConfigPath(partition))
            .Take(1)
            .SelectMany(sync =>
                AccessAssignmentGuard.IsSystemOwned(sync, hub.JsonSerializerOptions)
                    ? SystemOwnedIsExempt(partition, node.Id)
                    : LastAdminVerdict(meshService, partition, node, context.Operation))
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
    /// The system-owned outcome: allowed, and said out loud. Access disappearing silently is its
    /// own bug class, and this is the branch that lets the last human Admin of a repo-owned space
    /// actually go — so it states which rule permitted it rather than looking like no rule ran.
    /// </summary>
    private IObservable<NodeValidationResult> SystemOwnedIsExempt(string partition, string id)
    {
        logger?.LogInformation(
            "Allowed last-admin removal on SYSTEM-OWNED partition '{Partition}' (assignment "
            + "'{Id}'): it has {Partition}/_GitSync and is rewritten from its repo on every sync, "
            + "so its administrator is the importer identity, not a person.",
            partition, id, partition);
        return Observable.Return(NodeValidationResult.Valid());
    }

    /// <summary>
    /// The ordinary verdict: block when no OTHER non-denied Admin assignment remains in
    /// <c>{partition}/_Access</c>.
    /// </summary>
    private IObservable<NodeValidationResult> LastAdminVerdict(
        IMeshService meshService, string partition, MeshNode node, NodeOperation operation)
    {
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
                    partition, node.Id, operation);
                return NodeValidationResult.Invalid(
                    $"Cannot remove the last administrator of '{partition}'. " +
                    "A space must always have at least one admin — grant another user the Admin role first.",
                    NodeRejectionReason.ValidationFailed);
            });
        // No Catch here: the caller wraps BOTH reads — the system-owned probe and this count — in
        // the one fall-through-to-Valid handler, so a fault in either takes the same documented
        // path. A second handler here would swallow the probe's fault before the caller saw it.
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
    /// <c>Admin</c> role with <see cref="RoleAssignment.Denied"/> = false (the shared
    /// <see cref="AccessAssignmentGuard.GrantsAdmin"/> predicate, so the system-owned sweep
    /// counts admins the same way this invariant does). Tolerates content still carried as a
    /// <see cref="JsonElement"/> (source hub without the typed registry).
    /// </summary>
    private bool GrantsAdmin(MeshNode? node)
    {
        var assignment = node?.Content switch
        {
            AccessAssignment aa => aa,
            JsonElement je => TryDeserialize(je),
            _ => null,
        };
        return AccessAssignmentGuard.GrantsAdmin(assignment);
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
