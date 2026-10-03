using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Storage;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph.Storage;

/// <summary>
/// Access to a storage binding is administration of the partition it belongs to: EVERY operation
/// — read included — needs <see cref="Permission.Update"/> on the partition ROOT, the first segment
/// of the binding's own path. Never the node's <c>MainNode</c> field, which a writer supplies: a
/// binding is placed by its path, so its path decides who administers it.
///
/// <para>For <c>Admin/_Storage</c> that is Update on <c>Admin</c>, which only a platform admin
/// holds. A platform admin holds NOTHING on another partition by that role (a global admin has no
/// data access), so this rule gives them no read of a Space's bindings either.</para>
/// </summary>
/// <param name="hub">The mesh hub the permission is checked on.</param>
internal sealed class StorageBindingAccessRule(IMessageHub hub) : INodeTypeAccessRule
{
    /// <inheritdoc />
    public string NodeType => StorageBindingPaths.NodeType;

    /// <inheritdoc />
    public IReadOnlyCollection<NodeOperation> SupportedOperations =>
        [NodeOperation.Read, NodeOperation.Create, NodeOperation.Update, NodeOperation.Delete];

    /// <inheritdoc />
    public IObservable<bool> HasAccess(NodeValidationContext context, string? userId)
    {
        var root = StorageBindingPaths.PartitionOf(context.Node.Path);
        if (string.IsNullOrEmpty(root))
            return Observable.Return(false);
        return hub.CheckPermission(root, string.IsNullOrEmpty(userId) ? WellKnownUsers.Anonymous : userId,
            Permission.Update);
    }
}
