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
///
/// <para>🚨 This rule NARROWS the fold: a Viewer holds Read on the partition and is still refused a
/// binding (it names vault secrets, endpoints and identity client ids). So it is
/// <see cref="INodeTypeExclusiveRead"/> — a Read grant never LISTS a binding either — and it declares the
/// one predicate that keeps the partition's administrators listing them (Doc/Architecture/AccessControl →
/// "Rule read predicates").</para>
/// </summary>
/// <param name="hub">The mesh hub the permission is checked on.</param>
internal sealed class StorageBindingAccessRule(IMessageHub hub)
    : INodeTypeAccessRule, INodeTypeReadPredicates, INodeTypeExclusiveRead
{
    /// <inheritdoc />
    public string NodeType => StorageBindingPaths.NodeType;

    /// <summary>
    /// The rows a LISTING may return: a binding whose partition ROOT the caller holds Update on — the same
    /// root and permission <see cref="HasAccess"/> checks, so predicate ⇒ HasAccess. Applied by a store only
    /// when it supports <see cref="RuleReadSubject.PermissionOnPartitionRoot"/>; an older store lists no
    /// binding to anyone but System (fail closed).
    /// </summary>
    public IReadOnlyList<RuleReadPredicate> ReadPredicates =>
    [
        new()
        {
            NodeType = StorageBindingPaths.NodeType,
            Subject = RuleReadSubject.PermissionOnPartitionRoot,
            HeldPermission = Permission.Update,
        },
    ];

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
