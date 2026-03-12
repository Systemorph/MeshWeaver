using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Graph.Security;

/// <summary>
/// Reusable access rule for satellite node types (Activity, UserActivity, Kernel, Portal).
/// Delegates all access checks to the MainNode (parent node):
/// - Read: requires Read on MainNode
/// - Create/Update/Delete: requires Update on MainNode
/// </summary>
public class SatelliteAccessRule(string nodeType, ISecurityService securityService) : INodeTypeAccessRule
{
    public string NodeType => nodeType;

    public IReadOnlyCollection<NodeOperation> SupportedOperations =>
        [NodeOperation.Read, NodeOperation.Create, NodeOperation.Update, NodeOperation.Delete];

    public async Task<bool> HasAccessAsync(NodeValidationContext context, string? userId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(userId))
            return false;

        var mainNodePath = context.Node.MainNode;
        if (string.IsNullOrEmpty(mainNodePath) || mainNodePath == context.Node.Path)
            return false;

        var permission = context.Operation switch
        {
            NodeOperation.Read => Permission.Read,
            _ => Permission.Update
        };

        return await securityService.HasPermissionAsync(mainNodePath, userId, permission, ct);
    }
}
