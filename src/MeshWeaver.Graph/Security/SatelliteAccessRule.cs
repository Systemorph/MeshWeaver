using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;

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
public class SatelliteAccessRule(string nodeType, SecurityService securityService) : INodeTypeAccessRule
{
    public string NodeType => nodeType;

    public IReadOnlyCollection<NodeOperation> SupportedOperations =>
        [NodeOperation.Read, NodeOperation.Create, NodeOperation.Update, NodeOperation.Delete];

    public IObservable<bool> HasAccess(NodeValidationContext context, string? userId)
    {
        if (string.IsNullOrEmpty(userId))
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
                : securityService.HasPermission(fallbackPath, userId, fallbackPermission);
        }

        var permission = MainNodeDelegatedPermission(context);
        return securityService.HasPermission(mainNodePath, userId, permission);
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
