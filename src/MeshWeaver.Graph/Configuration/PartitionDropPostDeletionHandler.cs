using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Tears down a partition's entire backing store when its partition-owning root node is
/// deleted — the deletion-side mirror of <c>OwnsPartitionProvisioningValidator</c>. Deleting
/// a <c>Space</c> removes the space's nodes (the recursive delete), and this handler then
/// (1) drops the partition's backing store on every <see cref="IPartitionStorageProvider"/>
/// (the Postgres provider drops the schema with all satellite tables — threads, access,
/// activities — in one <c>DROP SCHEMA … CASCADE</c>), and (2) deletes the
/// <c>Admin/Partition/{id}</c> <see cref="PartitionDefinition"/> node that
/// <c>SpacePostCreationHandler</c> emitted, so the partition disappears from the routing
/// prime and partition listings mesh-wide.
///
/// <para>Sequenced store-drop → definition-delete: when the store drop fails, the
/// definition node stays so the partition remains visible for a retry instead of turning
/// into an invisible orphan schema. The definition delete runs under
/// <c>ImpersonateAsSystem</c> (infrastructure cleanup in the Admin partition — the deleting
/// user legitimately may not hold Admin-partition rights). 100% reactive — no
/// <c>async</c>/<c>await</c>; the async DDL edge is sealed inside each provider's
/// <c>IIoPool</c>.</para>
/// </summary>
public sealed class PartitionDropPostDeletionHandler(
    IMessageHub hub,
    ILogger<PartitionDropPostDeletionHandler>? logger = null) : INodePostDeletionHandler
{
    /// <inheritdoc />
    public string NodeType => SpaceNodeType.NodeType;

    /// <inheritdoc />
    public IObservable<Unit> Handle(MeshNode deletedNode, string? deletedBy)
    {
        // Only a partition ROOT (top-level: namespace empty, path == id) owns a partition.
        // A nested node of this type (which OwnsPartitionProvisioningValidator rejects on
        // create anyway) must never drop the enclosing partition.
        if (!string.IsNullOrEmpty(deletedNode.Namespace) || string.IsNullOrEmpty(deletedNode.Id))
            return Observable.Return(Unit.Default);

        var partition = deletedNode.Id;
        var providers = hub.ServiceProvider.GetServices<IPartitionStorageProvider>().ToList();

        // Sequential (.Concat) like provisioning, so concurrent DDL never races. A provider
        // failure propagates — the delete pipeline surfaces it as a Warning on the activity.
        var dropStores = providers
            .Select(p => p.DeletePartition(partition))
            .Concat()
            .ToList()
            .Do(_ => logger?.LogInformation(
                "Dropped partition '{Partition}' across {Count} provider(s) after {NodeType} deletion by {User}",
                partition, providers.Count, deletedNode.NodeType, deletedBy ?? "system"))
            .Select(_ => Unit.Default);

        return dropStores.Concat(DeletePartitionDefinition(partition)).TakeLast(1);
    }

    /// <summary>
    /// Deletes the <c>Admin/Partition/{partition}</c> definition node under System
    /// impersonation. Best-effort: an absent definition (e.g. a bootstrap-created partition
    /// that never got one) or a failed delete is logged but does not fail the handler —
    /// the backing store is already gone, which is the part that matters.
    /// </summary>
    private IObservable<Unit> DeletePartitionDefinition(string partition)
    {
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        var defPath = $"{PartitionNodeType.Namespace}/{partition}";

        return Observable.Using(
                () => accessService.ImpersonateAsSystem(),
                _ => meshService.DeleteNode(defPath))
            .Do(deleted => logger?.LogInformation(
                "Partition definition '{Path}' delete after partition drop: {Result}",
                defPath, deleted ? "deleted" : "not deleted"))
            .Catch<bool, Exception>(ex =>
            {
                logger?.LogWarning(ex,
                    "Could not delete partition definition '{Path}' after dropping partition '{Partition}'",
                    defPath, partition);
                return Observable.Return(false);
            })
            .Select(_ => Unit.Default);
    }
}
