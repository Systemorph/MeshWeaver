using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph.Security;

/// <summary>
/// 🚨 <b>A platform admin may ISSUE an access grant on a partition nobody else can grant on</b> — an
/// OWNERLESS one (no grants, no policy) or a SYSTEM-OWNED one (a one-way <c>_GitSync</c>) — and on
/// no other (#5904).
///
/// <para><b>The dead end this opens.</b> Issuing a grant is a Create under <c>{partition}/_Access</c>,
/// which the path fold decides on that parent. On both partition shapes nobody holds it:
/// an ownerless partition denies everyone, and a system-owned one grants write to the importer only
/// (<c>AccessAssignmentGuard.IsForbiddenOnSystemOwned</c> refuses every Admin/Editor grant and
/// <c>SystemOwnedAccessRetractionHandler</c> retracts the ones that predate the sync). A platform
/// admin is deliberately NOT a data superuser, so their Admin-partition grant reaches nothing there
/// either. Measured on partnerre-control 2026-09-29/30: Space <c>Deployments</c>, created over MCP and
/// GitSynced a moment later, recorded no grant; the refusal told the platform admin that "a platform
/// admin" must grant access under <c>Deployments/_Access</c> — and refused that very grant. Every
/// Hosting InstanceAction reading <c>Deployments/*</c> as its approver then became unapprovable.</para>
///
/// <para><b>Why this is not a superuser read.</b> Nothing is widened implicitly: the admin gains
/// nothing until they write an explicit, audited <c>AccessAssignment</c> node, the same record any
/// grant is. And the grant stays bounded by every other validator in the chain — on a system-owned
/// partition <c>IsForbiddenOnSystemOwned</c> still refuses anything that confers write, so what a
/// platform admin can issue there is a <c>Viewer</c>/<c>Commenter</c> entitlement, exactly what that
/// refusal prescribes. On a partition that HAS an owner (grants or a policy) and no one-way sync,
/// this answers <c>false</c> and the owner stays the only one who grants.</para>
///
/// <para><b>Cost.</b> Consulted only after the path fold DENIED a Create of an
/// <c>AccessAssignment</c>: an ordinary grant by a partition admin never reaches it. The admin probe
/// (a fold on the Admin scope) runs before the storage probes, so a non-admin's denial pays one
/// fold and no reads.</para>
/// </summary>
public static class PlatformAdminGrantRepair
{
    /// <summary>
    /// True when <paramref name="userId"/> — a platform admin — may create the grant node in
    /// <paramref name="context"/> although the path fold refused it. See the type's remarks.
    /// </summary>
    /// <param name="hub">The hub whose service provider resolves the fold and the storage adapter.</param>
    /// <param name="context">The refused operation.</param>
    /// <param name="userId">The refused principal.</param>
    /// <returns>An observable emitting ONE answer; <c>false</c> on every indeterminate probe.</returns>
    public static IObservable<bool> MayIssue(IMessageHub hub, NodeValidationContext context, string? userId)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(context);

        if (context.Operation != NodeOperation.Create
            || !string.Equals(context.Node.NodeType, AccessAssignmentGuard.AccessAssignmentNodeType,
                StringComparison.OrdinalIgnoreCase)
            || userId is null
            || !WellKnownUsers.IsAuthenticated(userId))
            return Observable.Return(false);

        // Only a grant FILED as one: `{scope}/_Access/{id}`. A root-scope grant (`_Access/x`, scope
        // "") is the data-superuser shape and never reaches here with an answer of yes.
        var scope = AccessAssignmentGuard.ScopeFromPath(context.Node.Path);
        if (string.IsNullOrEmpty(scope))
            return Observable.Return(false);
        var partition = AccessAssignmentGuard.PartitionOf(scope);
        if (string.IsNullOrEmpty(partition) || IsPlatformPartition(partition))
            return Observable.Return(false);

        // 🚨 FAIL CLOSED on content: a grant whose content does not read as an AccessAssignment is
        // not a grant this repair can reason about (the fold skips it; the cap below could not
        // see what it confers). Absent or undeserialisable content ⇒ no repair.
        if (context.Node.ContentAs<AccessAssignment>(hub.JsonSerializerOptions) is not { } assignment)
            return Observable.Return(false);

        // On a FLEET partition the repair issues an entitlement and nothing more — even where no
        // one-way sync would already bound it (IsForbiddenOnSystemOwned only speaks for a
        // system-owned partition). An ownerless `Ops` must not become somebody's by this road.
        if (WellKnownPartitions.Fleet.Contains(partition)
            && AccessAssignmentGuard.ConfersWriteAccess(assignment))
            return Observable.Return(false);

        return hub.IsGlobalAdmin(userId)
            .Take(1)
            .DefaultIfEmpty(false)
            .SelectMany(isAdmin => isAdmin
                ? PartitionWriteGuardValidator.IsUngrantable(hub, partition)
                    .Take(1)
                    .DefaultIfEmpty(false)
                : Observable.Return(false))
            .Catch<bool, Exception>(_ => Observable.Return(false));
    }

    /// <summary>True for a partition the repair refuses outright — see <see cref="WellKnownPartitions.Platform"/>.</summary>
    /// <param name="partition">The top-level partition.</param>
    /// <returns>Whether no platform-admin grant is ever issued there by this road.</returns>
    public static bool IsPlatformPartition(string partition)
        => partition.StartsWith('_')
           || WellKnownPartitions.IsMirror(partition)
           || WellKnownPartitions.Platform.Contains(partition);
}
