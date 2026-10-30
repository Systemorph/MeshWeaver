using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph.Security;

/// <summary>
/// Reusable access rule for satellite node types (Activity, UserActivity, Kernel,
/// Portal, Thread, ThreadMessage, Comment, Approval, TrackedChange).
/// Delegates all access checks to the MainNode (parent node):
/// - Read: requires Read on MainNode
/// - Create: requires the operation's natural permission on MainNode (Comment
///   for Comment nodes, Thread for Thread/ThreadMessage nodes, Update otherwise)
/// - Update/Delete: requires Update on MainNode
/// A policy on the path between the MainNode and where the satellite lands still applies: while one
/// restricts, the same permission must also hold at the landing path (stricter outcome wins).
/// </summary>
public class SatelliteAccessRule(string nodeType, IMessageHub hub) : INodeTypeAccessRule
{
    /// <summary>The node-type identifier this access rule applies to.</summary>
    public string NodeType => nodeType;

    /// <summary>The node operations this rule applies to: Read, Create, Update, and Delete.</summary>
    public IReadOnlyCollection<NodeOperation> SupportedOperations =>
        [NodeOperation.Read, NodeOperation.Create, NodeOperation.Update, NodeOperation.Delete];

    /// <summary>
    /// Determines whether the user may perform the operation on the satellite node by
    /// delegating the permission check to its MainNode (parent), falling back to a
    /// path-based check when the MainNode is absent or self-referencing.
    /// </summary>
    /// <param name="context">The validation context describing the node and operation.</param>
    /// <param name="userId">The identifier of the user requesting access, or null if anonymous.</param>
    /// <returns>An observable that emits whether access is permitted.</returns>
    public IObservable<bool> HasAccess(NodeValidationContext context, string? userId)
    {
        // A missing identity is ANONYMOUS — not an automatic deny. The standard
        // path-based check (RlsNodeValidator.CheckPermission) evaluates a null
        // user as WellKnownUsers.Anonymous, so a partition whose policy grants
        // PublicRead is readable by logged-out viewers. Hard-denying here made
        // every satellite STRICTER than its MainNode: a public-read viewer could
        // read a Code node but never its `_Activity` satellite, so the embedded
        // run-output pane hung on its spinner forever (2026-07-03). Delegating
        // with the Anonymous identity keeps closed-by-default semantics: only a
        // PublicRead policy (or an explicit Anonymous role grant) yields Read,
        // and write-class operations still require Update on the MainNode,
        // which Anonymous never holds unless explicitly granted.
        if (string.IsNullOrEmpty(userId))
            userId = WellKnownUsers.Anonymous;

        // A comment's AUTHOR may always delete their OWN comment — even with only Comment permission on
        // the document. Without this, Comment Delete falls through to the MainNode-delegated
        // Permission.Update below, so a commenter (no Update on the doc) sees the ✕ Delete button but the
        // delete is denied and silently swallowed — the "no delete works" half of issue #391. Match by
        // display Name, the SAME author identity the comment UI uses (Comment.Author is set from
        // AccessContext.Name). Non-authors still need Update (editors/admins), so the effective rule is
        // "author and admins can delete".
        if (context.Operation == NodeOperation.Delete
            && nodeType == "Comment"
            && !string.IsNullOrEmpty(context.AccessContext?.Name))
        {
            var comment = context.Node.ContentAs<Comment>(hub.JsonSerializerOptions);
            if (comment is not null
                && string.Equals(comment.Author, context.AccessContext!.Name, StringComparison.OrdinalIgnoreCase))
                return Observable.Return(true);
        }

        // 🛡️ An update never CHOOSES a satellite's main node (MainNodeTrust.Repoints). The stored pointer
        // governs the satellite — who reads it, who writes it, where its grants project — so a write that
        // moved it would decide access by a pointer its writer picked: a satellite could be moved under a
        // main node its writer cannot write to, or pointed at one whose grants it then borrows. This is
        // the update half of the create rule below: a create derives ownership from the path, an update
        // from the stored node. The one change allowed is the path-derived one (an unset self-default
        // normalised to the owner), which is decided like any other update of the stored node.
        if (context.Operation == NodeOperation.Update
            && context.ExistingNode is { } stored
            && MainNodeTrust.Repoints(stored, context.Node))
            return Observable.Return(false);

        var mainNodePath = context.Node.MainNode;
        // Degenerate case: no MainNode (or self-referencing). The rule has no
        // opinion — fall back to the same path-based check the validator would
        // apply if no custom rule were registered (parent path for Create, own
        // path otherwise). Without this fallback, satellite NodeTypes with a
        // missing MainNode are unconditionally denied — including for admin
        // users — which breaks tests that construct nodes directly and any
        // legacy data lacking the MainNode pointer.
        if (string.IsNullOrEmpty(mainNodePath) || mainNodePath == context.Node.Path)
        {
            var fallbackPath = context.Operation == NodeOperation.Create
                ? (context.Node.GetParentPath() ?? context.Node.Path)
                : context.Node.Path;
            var fallbackPermission = StandardPathPermission(context);
            return fallbackPermission == Permission.None
                ? Observable.Return(true)
                : hub.CheckPermission(fallbackPath, userId, fallbackPermission);
        }

        var permission = MainNodeDelegatedPermission(context);
        var delegated = WithInterveningPolicies(
            context, userId, mainNodePath, permission, hub.CheckPermission(mainNodePath, userId, permission));

        // 🛡️ A main node the WRITER is choosing on a create (an update that changes it is refused
        // above) that is not the satellite's OWNER is not enough on its own (review of #7601).
        // MainNode is a field the writer supplies: a comment in another person's partition naming the writer's own home
        // would otherwise be authorised against the writer's home rather than where it lands. Such a
        // write must ALSO pass the path check where the node lands. "Owner" is exact and ordinal
        // (MainNodeTrust.IsOwnerOf): an ancestor further up is not enough, since a scope in between can
        // break the inherited grant, and a case-variant path is a different node.
        // A STORED pointer (a read, a delete, an update that keeps it) is delegated to as before — that
        // is how an off-partition main node legitimately governs its satellites. An Update without an
        // ExistingNode is a decision about the STORED node itself (the delivery gate and the verbs'
        // pre-flights build it that way). A routed write that reaches the owner never meets this rule
        // with its proposal, so the same refusal is held at the owner's commit point too
        // (SatelliteMainNodeCommitGuard).
        var writerChoosesPointer = context.Operation == NodeOperation.Create;
        if (!writerChoosesPointer || MainNodeTrust.IsOwnerOf(mainNodePath, context.Node.Path))
            return delegated;

        var landingPath = context.Node.GetParentPath() ?? context.Node.Path;
        var landingPermission = StandardPathPermission(context);
        var landing = landingPermission == Permission.None
            ? Observable.Return(true)
            : hub.CheckPermission(landingPath, userId, landingPermission);
        return delegated.CombineLatest(landing, (viaMainNode, atLanding) => viaMainNode && atLanding);
    }

    /// <summary>
    /// 🛡️ Delegation to the main node never skips a policy on the path in between. When the main node
    /// is an ANCESTOR of where the satellite lands (its parent on a create, its own path otherwise), a
    /// policy on a scope between the two — an inheritance break on an intermediate segment, a cap on a
    /// mapped container such as <c>Doc/_Comment</c> — would have restricted the satellite had it been
    /// decided by its path, and the main node's permission alone never sees it. So while such a
    /// policy exists, the same permission must ALSO hold where the satellite lands, and the stricter
    /// outcome wins. Without one the landing check is not run: the landing inherits every grant of the
    /// main node, and skipping it keeps additive surfaces that are declared on the main node alone (a
    /// type gate's public cover) reaching the satellites they always reached.
    /// </summary>
    private IObservable<bool> WithInterveningPolicies(
        NodeValidationContext context, string userId, string mainNodePath, Permission permission,
        IObservable<bool> viaMainNode)
    {
        var landingPath = context.Operation == NodeOperation.Create
            ? (context.Node.GetParentPath() ?? context.Node.Path)
            : context.Node.Path;
        // No row-level security on this mesh: every check answers All, so there is nothing to apply.
        if (hub.Configuration.Get<EffectivePermissionsDelegate>() is null
            || string.IsNullOrEmpty(landingPath)
            || string.Equals(mainNodePath.Trim('/'), landingPath.Trim('/'), StringComparison.Ordinal)
            || !MainNodeTrust.IsAncestorOrSelf(mainNodePath, landingPath))
            return viaMainNode;

        var atLanding = PermissionEvaluator.ObserveRestrictingPolicyBetween(hub, mainNodePath, landingPath)
            .Select(restricted => restricted
                ? hub.CheckPermission(landingPath, userId, permission)
                : Observable.Return(true))
            .Switch();
        return viaMainNode.CombineLatest(atLanding, (main, landing) => main && landing);
    }

    /// <summary>
    /// Permission required on the satellite's MainNode for the requested
    /// operation. Satellite-creation semantics: creating a satellite is a
    /// MODIFICATION of the parent (MainNode), so Create maps to Update — except
    /// for Comment nodes, where the dedicated Permission.Comment grants
    /// commenters the ability to add comments without full Update rights.
    /// </summary>
    private static Permission MainNodeDelegatedPermission(NodeValidationContext context) => context.Operation switch
    {
        NodeOperation.Read => Permission.Read,
        NodeOperation.Create => context.Node.NodeType == "Comment"
            ? Permission.Comment
            : Permission.Update,
        _ => Permission.Update
    };

    /// <summary>
    /// Permission required on the node's own path when MainNode is absent —
    /// matches <c>RlsNodeValidator.GetCreatePermission</c> + the standard
    /// path-based mapping so the fallback behaves identically to "no custom
    /// rule registered".
    /// </summary>
    private static Permission StandardPathPermission(NodeValidationContext context) => context.Operation switch
    {
        NodeOperation.Read => Permission.Read,
        NodeOperation.Create => context.Node.NodeType == "Comment"
            ? Permission.Comment
            : Permission.Create,
        NodeOperation.Update => Permission.Update,
        NodeOperation.Delete => DeletePermission.RequiredFor(context.Node.Path),
        _ => Permission.None
    };
}
