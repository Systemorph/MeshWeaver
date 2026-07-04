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
        return hub.CheckPermission(mainNodePath, userId, permission);
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
        NodeOperation.Delete => Permission.Delete,
        _ => Permission.None
    };
}
