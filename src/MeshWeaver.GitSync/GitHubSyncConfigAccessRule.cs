using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

namespace MeshWeaver.GitSync;

/// <summary>
/// The access rule for a Space's GitHub-sync config (<c>{space}/_GitSync</c>,
/// <see cref="GitHubSyncService.ConfigNodeType"/>): <b>a platform admin can ALWAYS see it and
/// delete it</b>; everybody else exactly as before.
///
/// <para><b>Why a rule at all.</b> The config node had none, so every seam fell through to the
/// path fold: Read demanded <see cref="Permission.Read"/> on <c>{space}/_GitSync</c>
/// (<c>RlsNodeValidator</c>), Delete demanded <see cref="Permission.Delete"/> on the Space
/// (<c>MeshExtensions.CheckDeletePermissionByPath</c> on <c>MainNode</c>, plus the
/// <c>[RequiresPermission(Delete)]</c> delivery gate on the node's own path) — and a ONE-WAY sync
/// makes the Space SYSTEM-OWNED, so nobody but <see cref="WellKnownUsers.System"/> can hold either:
/// <c>AccessAssignmentGuard.IsForbiddenOnSystemOwned</c> refuses to write an Admin/Editor grant
/// there and <see cref="SystemOwnedAccessRetractionHandler"/> retracts the ones that predate the
/// sync. A platform admin is deliberately NOT a data superuser (<c>hub.IsGlobalAdmin</c> confers no
/// Read on any other partition), so the very persona who operates the sync machinery got
/// <c>Not found</c> on <c>get</c> and <i>"Delete permission denied for '{space}/_GitSync'"</i> on
/// delete — measured on memex.meshweaver.cloud 2026-09-12 on <c>MeshWeaver/_GitSync</c>, a config
/// the platform itself had created that was re-importing the whole core repository on every green
/// build (Memex#237), and that no human could remove through any API.</para>
///
/// <para><b>What widens, and for whom.</b> Only the platform admin, and only on THIS node type:
/// the same <c>hub.IsGlobalAdmin</c> OR that <c>GitHubActivityExtensions.TriggerAuthorizedAsSystem</c>
/// already applies to every sync trigger ("triggering a sync is a platform action") now also covers
/// seeing the config and removing it. Update stays on the path fold — an admin still cannot repoint
/// somebody's sync — and the Space's CONTENT stays gated: a sync config carries the repo, branch and
/// last-sync state, never a credential (that is the separate <see cref="GitHubCredential"/> node
/// in the owner's own partition).</para>
///
/// <para><b>For everybody else nothing changes.</b> The non-admin leg is the fold on the node's
/// own path, which is what the RLS validator and the delivery gate demanded before; a Delete on
/// <c>{space}/_GitSync</c> inherits the Space's grants, so it is the pre-flight's demand on
/// <c>MainNode</c> too. Closed by default, as it was.</para>
///
/// <para>Registered in <c>AddGitHubSyncServices</c> — the service-collection entry point every
/// host composes — next to the retraction handler that creates the situation this rule resolves.
/// Consulted through <see cref="NodeTypeAccessRuleGate"/> by all three seams (RLS read filter,
/// delivery gate, delete pre-flight), so they cannot answer differently (#3061).</para>
/// </summary>
public sealed class GitHubSyncConfigAccessRule(IMessageHub hub) : INodeTypeAccessRule
{
    /// <inheritdoc />
    public string NodeType => GitHubSyncService.ConfigNodeType;

    /// <summary>
    /// Read and Delete only. Create and Update deliberately stay on the standard check: this rule
    /// exists so an operator can SEE and REMOVE a sync, not so a platform grant becomes a licence
    /// to rewire one.
    /// </summary>
    public IReadOnlyCollection<NodeOperation> SupportedOperations =>
        [NodeOperation.Read, NodeOperation.Delete];

    /// <inheritdoc />
    public IObservable<bool> HasAccess(NodeValidationContext context, string? userId)
    {
        if (string.IsNullOrEmpty(userId) || userId == WellKnownUsers.Anonymous)
            return Observable.Return(false);

        var required = context.Operation switch
        {
            NodeOperation.Read => Permission.Read,
            NodeOperation.Delete => Permission.Delete,
            _ => Permission.None
        };
        if (required == Permission.None)
            return Observable.Return(false);

        // The ordinary check first, the platform-admin OR second, and the second only when the first
        // said no — the admin probe is a second fold (on the Admin scope) and the common case is a
        // caller who holds the grant. Take(1) on each leg: both ride the live AccessAssignment
        // query and never complete on their own; the caller (NodeTypeAccessRuleGate.Evaluate)
        // takes ONE decision off the gate, and an empty leg is reported as Undetermined — fail
        // closed — never as a grant.
        return hub.CheckPermission(context.Node.Path, userId, required)
            .Take(1)
            .SelectMany(granted => granted
                ? Observable.Return(true)
                : hub.IsGlobalAdmin(userId).Take(1));
    }
}
