using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Tears down a partition's entire backing store when its partition-owning root node is
/// deleted — the deletion-side mirror of <c>OwnsPartitionProvisioningValidator</c>. Deleting
/// a partition ROOT removes that partition's nodes (the recursive delete), and this handler
/// then
/// (1) drops the partition's backing store on every <see cref="IPartitionStorageProvider"/>
/// (the Postgres provider drops the schema with all satellite tables — threads, access,
/// activities, notifications, user_activities — in one <c>DROP SCHEMA … CASCADE</c>), and
/// (2) deletes the <c>Admin/Partition/{id}</c> <see cref="PartitionDefinition"/> node that
/// <c>SpacePostCreationHandler</c> emitted (absent for some roots — the definition delete is
/// best-effort), so the partition disappears from the routing prime and partition listings
/// mesh-wide.
///
/// <para><b>Why the DROP is essential (not merely tidy).</b> The recursive fan-out only
/// enumerates <c>mesh_nodes</c> descendants; satellite-table rows (threads, notifications,
/// user_activities, access) live in dedicated tables the fan-out never visits, and RLS can
/// hide gated children from the enumeration. The unconditional <c>DROP SCHEMA … CASCADE</c>
/// is what makes a partition-root delete COMPLETE regardless — no orphaned schema, no leaked
/// satellite rows.</para>
///
/// <para>🚨 <b>It matches STRUCTURALLY, and that is the whole fix (#3436).</b> This handler
/// used to be registered ONCE PER NODETYPE, with the matched type supplied at the registration
/// site — <c>AddSpaceType</c> → <c>Space</c>, <c>AddUserType</c> → <c>User</c>. Every partition
/// root of any OTHER type therefore deleted its nodes and left its schema behind, silently:
/// <list type="bullet">
///   <item>a <c>User</c> home was left entirely behind on 2026-07-19 (memex-cloud), and the fix
///     applied was a SECOND hand-written registration rather than the generalisation;</item>
///   <item>on 2026-09-06 four <c>Store/Plugin</c>-rooted partitions (<c>AgenticPrimerDe</c>,
///     <c>DataImportExport</c>, <c>DataModeling</c>, <c>ThinkInStreams</c>) were deleted on the
///     systemorph staff portal and kept their Postgres schemas AND their
///     <c>Admin/Partition</c> definitions.</item>
/// </list>
/// <c>Store/Plugin</c> is declared in mesh CONTENT (the plugins package), so no <c>src/</c>-side
/// enumeration could ever have listed it — and it does not set
/// <c>NodeTypeDefinition.OwnsPartition</c> either, because the PACKAGE INSTALLER provisioned its
/// schema (<c>PackageInstaller.EnsurePartitionsProvisioned</c>), not
/// <c>OwnsPartitionProvisioningValidator</c>. Driving the registration off <c>OwnsPartition</c>
/// would therefore STILL have missed it. The only predicate with no blind spot is the structural
/// one: a partition is a first path segment, so a top-level node IS its partition's root —
/// whatever its type, and whether that type existed at boot or arrived from the mesh afterwards.
/// See <see cref="PartitionDefinition.IsPartitionRoot"/> and
/// <c>Doc/Architecture/PartitionTeardown</c>.</para>
///
/// <para>Registered ONCE, by <c>AddGraph</c>, next to the boot gate
/// (<see cref="PartitionTeardownCoverageGate"/>) that refuses to start a mesh whose handler set
/// does not cover an arbitrary partition root.</para>
///
/// <para>Sequenced store-drop → definition-delete: when the store drop fails, the
/// definition node stays so the partition remains visible for a retry instead of turning
/// into an invisible orphan schema. The definition delete runs under
/// <c>ImpersonateAsSystem</c> (infrastructure cleanup in the Admin partition — the deleting
/// user legitimately may not hold Admin-partition rights). 100% reactive — no
/// <c>async</c>/<c>await</c>; the async DDL edge is sealed inside each provider's
/// <c>IIoPool</c>.</para>
/// </summary>
public sealed class PartitionDropPostDeletionHandler : INodePostDeletionHandler
{
    /// <summary>
    /// The <see cref="INodePostDeletionHandler.NodeType"/> label of a handler that matches
    /// STRUCTURALLY rather than by type name. Diagnostic only — <see cref="Matches"/> never
    /// reads it.
    /// </summary>
    public const string AnyPartitionRoot = "*";

    private readonly IMessageHub hub;
    private readonly ILogger<PartitionDropPostDeletionHandler>? logger;

    /// <summary>
    /// Creates the partition teardown handler. It matches every partition ROOT
    /// (<see cref="PartitionDefinition.IsPartitionRoot"/>) regardless of NodeType.
    /// </summary>
    /// <param name="hub">Hub supplying the storage providers, mesh service and access service.</param>
    /// <param name="logger">Diagnostics.</param>
    public PartitionDropPostDeletionHandler(
        IMessageHub hub,
        ILogger<PartitionDropPostDeletionHandler>? logger = null)
    {
        this.hub = hub;
        this.logger = logger;
    }

    /// <summary>
    /// Legacy per-NodeType construction. <paramref name="nodeType"/> is IGNORED: the teardown
    /// now matches every partition root structurally, which is strictly more coverage and is the
    /// fix for #3436 (a per-type registration could never see an in-mesh NodeType such as
    /// <c>Store/Plugin</c>). Kept so an out-of-repo registration keeps compiling and keeps
    /// working; new code uses the two-argument constructor.
    /// </summary>
    /// <param name="hub">Hub supplying the storage providers, mesh service and access service.</param>
    /// <param name="nodeType">Ignored — retained for source compatibility.</param>
    /// <param name="logger">Diagnostics.</param>
    [Obsolete("The partition teardown matches every partition ROOT structurally; the nodeType "
              + "argument is ignored. Use the (hub, logger) constructor and register it once.")]
    public PartitionDropPostDeletionHandler(
        IMessageHub hub,
        string nodeType,
        ILogger<PartitionDropPostDeletionHandler>? logger = null)
        : this(hub, logger)
    {
        _ = nodeType;
    }

    /// <inheritdoc />
    public string NodeType => AnyPartitionRoot;

    /// <summary>
    /// Structural match: every partition ROOT, whatever its NodeType — see
    /// <see cref="PartitionDefinition.IsPartitionRoot"/>. A system-managed MIRROR partition
    /// (<c>Auth</c>, the <c>User</c> lookup mirror) is excluded: it is populated by a database
    /// trigger, has no owner, and is created by the migration rather than by a root write, so a
    /// root delete there must never drop it.
    /// </summary>
    /// <param name="deletedNode">The node as it was loaded before deletion.</param>
    /// <returns><c>true</c> when the deleted node is a tear-down-able partition root.</returns>
    public bool Matches(MeshNode deletedNode) =>
        PartitionDefinition.IsPartitionRoot(deletedNode)
        && !WellKnownPartitions.IsMirror(deletedNode.Id);

    /// <inheritdoc />
    public IObservable<Unit> Handle(MeshNode deletedNode, string? deletedBy)
    {
        // Defence in depth: Matches already answered this, but Handle is public and a caller
        // that invokes it directly must never drop the enclosing partition of a NESTED node.
        if (!Matches(deletedNode))
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
