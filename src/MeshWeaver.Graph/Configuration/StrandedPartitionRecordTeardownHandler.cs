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
/// Finishes the teardown of a partition that NO ordinary delete can reach any more, when its
/// RECORD — the <c>Admin/Partition/{partition}</c> <see cref="PartitionDefinition"/> node — is
/// deleted. The verb <see href="https://github.com/Systemorph/MeshWeaver/issues/5073">#5073</see>'s
/// second defect was missing.
///
/// <para><b>The state this exists for.</b> A partition root deleted BEFORE the structural teardown
/// existed (#3436) left everything else behind: the backing store (the Postgres schema), every row
/// in it — NodeType definitions included — and the <c>Admin/Partition</c> record. The delete
/// pipeline names that state itself, at Critical: <i>"the schema is now ORPHANED … drop partition
/// manually"</i>. But there was nothing to drop it WITH. The ordinary delete needs a root node,
/// and there is none; the first child write into the partition — the compile watcher cutting a
/// Release for one of the surviving NodeType rows is one — makes <c>EnsurePartitionBootstrap</c>
/// heal a <c>Space</c> root under System with no access grant (there is no creator to grant), so
/// the partition comes back as a shell nobody can see, own or delete. Every instrument then
/// reports it as live: the store is there, the root is there, so the bake sweep enumerates its
/// NodeType rows on every roll and every replica compiles them. That is how a partition that
/// "hasn't existed in ages" holds a pod's readiness (the incident) and how #5163's store witness
/// — correct for a DROPPED schema — is inert on the very partition it was written for.</para>
///
/// <para><b>Why the record is the right handle.</b> It is the one artefact of the partition that
/// OUTLIVES the data by design (<i>"the record outlives the data"</i>), it lives in the
/// <c>Admin</c> partition, so a platform admin can reach it with the ordinary delete
/// (<c>delete @Admin/Partition/{partition}</c>), and deleting it already meant "this partition is
/// gone" — it simply did not DO anything. Now it finishes what a partition-root delete would have
/// done: the same <see cref="PartitionDropPostDeletionHandler.DropStores"/> on every provider and
/// the same cache eviction, under the same tombstone the ordinary teardown holds so a concurrent
/// child write cannot heal a root back mid-drop (#3451).</para>
///
/// <para>🚨 <b>Only for a partition that is STRANDED, and that is what keeps a platform admin a
/// platform admin.</b> Global admin is <c>Permission.All</c> on the <c>Admin</c> partition, not a
/// data superuser: deleting the record of a LIVE, OWNED partition must not drop somebody's
/// schema. So the drop runs in exactly two shapes, both of which no owner can reach through the
/// partition's own root:</para>
/// <list type="number">
///   <item><description><b>No root at all</b> — no durable row and no static root serves the
///     partition path: the orphan as the pre-#3436 delete left it.</description></item>
///   <item><description><b>A healed shell</b> — a root carrying exactly the fingerprint
///     <c>HealPartitionRoot</c> writes (<c>Space</c>, named after the partition, no content,
///     created by System), with NO access grant at all (durable or static) and no GitSync
///     configuration (a synced partition is owned by the deploy). Nobody was granted anything on
///     it, so nobody can delete it through its root — and it can only have got that way by the
///     bootstrap re-rooting an orphan.</description></item>
/// </list>
/// <para>Anything else — a real root, an owner, a synced partition, a static partition — is
/// LIVE: the record delete is REFUSED to touch the store, and says so at Warning, because a live
/// partition's record disappearing is already a defect worth a line (it leaves the partition out
/// of every listing and the routing prime). Every probe fails CLOSED: an unreadable root or grant
/// listing reads as <i>live</i>, and nothing is dropped.</para>
///
/// <para><b>Not a second drop.</b> The ordinary teardown deletes this same record as its second
/// step, inside its own deletion scope; this handler reads the tombstone first and stands down
/// while a partition's deletion is in flight or on record, so the two never race for one schema.
/// 100% reactive; the async DDL edge stays sealed in each provider's <c>IIoPool</c>.</para>
/// </summary>
public sealed class StrandedPartitionRecordTeardownHandler : INodePostDeletionHandler
{
    private readonly IMessageHub hub;
    private readonly ILogger<StrandedPartitionRecordTeardownHandler>? logger;

    /// <summary>
    /// Creates the handler. It matches the deletion of an <c>Admin/Partition/{partition}</c>
    /// record and acts only when that partition is stranded.
    /// </summary>
    /// <param name="hub">Hub supplying the storage providers, the storage adapter and the tombstone registry.</param>
    /// <param name="logger">Diagnostics.</param>
    public StrandedPartitionRecordTeardownHandler(
        IMessageHub hub,
        ILogger<StrandedPartitionRecordTeardownHandler>? logger = null)
    {
        this.hub = hub;
        this.logger = logger;
    }

    /// <inheritdoc />
    public string NodeType => PartitionNodeType.NodeType;

    /// <summary>
    /// A <see cref="PartitionDefinition"/> record under <c>Admin/Partition</c> whose id is a valid,
    /// non-mirror partition segment. The mirrors (<c>Auth</c>, <c>User</c>) are populated by a
    /// database trigger and have no root by design — deleting their record must never drop them.
    /// </summary>
    /// <param name="deletedNode">The node as it was loaded before deletion.</param>
    /// <returns><c>true</c> when the deleted node is a partition record this handler may act on.</returns>
    public bool Matches(MeshNode deletedNode) =>
        string.Equals(deletedNode.NodeType, PartitionNodeType.NodeType, StringComparison.OrdinalIgnoreCase)
        && string.Equals(deletedNode.Namespace, PartitionNodeType.Namespace, StringComparison.OrdinalIgnoreCase)
        && PartitionDefinition.IsValidPartitionSegment(deletedNode.Id)
        && !WellKnownPartitions.IsMirror(deletedNode.Id);

    /// <inheritdoc />
    public IObservable<Unit> Handle(MeshNode deletedNode, string? deletedBy)
    {
        if (!Matches(deletedNode))
            return Observable.Return(Unit.Default);

        var partition = deletedNode.Id;
        var registry = hub.ServiceProvider.GetRequiredService<RecentlyDeletedRegistry>();

        // The ordinary teardown deletes this record as its own second step, inside its deletion
        // scope — the tombstone says so, and this handler stands down rather than dropping a store
        // the root delete is already dropping.
        if (registry.IsUnderActiveDeletion(partition, out var deletionRoot))
        {
            logger?.LogDebug(
                "[StrandedPartition] record of '{Partition}' deleted while its deletion is in flight "
                + "(subtree '{Root}') — the ordinary teardown owns the drop; nothing to do here",
                partition, deletionRoot);
            return Observable.Return(Unit.Default);
        }
        if (registry.IsRecentlyDeleted(partition))
        {
            logger?.LogDebug(
                "[StrandedPartition] record of '{Partition}' deleted right after the partition itself "
                + "was — the ordinary teardown owns the drop; nothing to do here",
                partition);
            return Observable.Return(Unit.Default);
        }

        return StrandedBecause(partition)
            .SelectMany(reason =>
            {
                if (reason is null)
                {
                    logger?.LogWarning(
                        "[StrandedPartition] the record 'Admin/Partition/{Partition}' was deleted by "
                        + "{User} but the partition is LIVE — it has a root somebody owns, or is served "
                        + "by configuration — so its backing store is NOT dropped. The partition is now "
                        + "missing from partition listings and the routing prime; to tear it down, "
                        + "delete its root node, which runs the structural teardown.",
                        partition, deletedBy ?? "system");
                    return Observable.Return(Unit.Default);
                }

                logger?.LogWarning(
                    "[StrandedPartition] finishing the teardown of '{Partition}' on the deletion of its "
                    + "record by {User}: {Reason}. No ordinary delete could reach it — dropping its "
                    + "backing store on every provider and evicting its cached queries (#5073).",
                    partition, deletedBy ?? "system", reason);

                // The same tombstone the ordinary teardown holds across its drop (#3451): marked
                // before the first DDL so a child write landing mid-drop is refused by the write
                // guard instead of healing a root back over a schema that is being dropped.
                return Observable.Using(
                    () => registry.BeginSubtreeDeletion(partition),
                    _ =>
                    {
                        registry.MarkDeleted(partition);
                        return PartitionDropPostDeletionHandler
                            .DropStores(hub, partition, $"the deletion of its record by {deletedBy ?? "system"} ({reason})", logger)
                            .Do(__ => PartitionDropPostDeletionHandler.DropCachedPartitionQueries(hub, partition, logger));
                    });
            });
    }

    /// <summary>
    /// Why <paramref name="partition"/> is stranded — one of the two shapes the class remark
    /// names — or <c>null</c> when it is live. Emits exactly once; every probe that cannot answer
    /// reads as LIVE, so a fault never drops a store.
    /// </summary>
    /// <param name="partition">The partition whose record was deleted.</param>
    internal IObservable<string?> StrandedBecause(string partition)
    {
        // A partition served by configuration is not data: its root (or its own record) is a
        // static node, and there is nothing in a store for this handler to finish.
        if (hub.ServiceProvider.FindStaticNode(partition) is { IsDefinitionOnly: false })
            return Observable.Return<string?>(null);

        var persistence = hub.ServiceProvider.GetService<IStorageAdapter>();
        if (persistence is null)
            return Observable.Return<string?>(null);
        var options = hub.JsonSerializerOptions;

        // Fail CLOSED on the root read: an unreadable store is a live partition for this purpose.
        // `Read` here is the same authoritative read EnsurePartitionBootstrap takes before it heals.
        var root = persistence.Read(partition, options)
            .Take(1)
            .Select(n => (Node: n, Readable: true))
            .Catch<(MeshNode? Node, bool Readable), Exception>(ex =>
            {
                logger?.LogDebug(ex,
                    "[StrandedPartition] could not read the root of '{Partition}'; treating it as live",
                    partition);
                return Observable.Return<(MeshNode? Node, bool Readable)>((null, false));
            })
            .DefaultIfEmpty((null, true));

        return root.SelectMany(r =>
        {
            if (!r.Readable)
                return Observable.Return<string?>(null);
            if (r.Node is null)
                return Observable.Return<string?>(
                    "its root node is gone and no static root serves it — the shape a partition-root "
                    + "delete left behind before the structural teardown existed");
            if (!IsHealedShell(r.Node, partition))
                return Observable.Return<string?>(null);

            // The heal's own fingerprint. It is stranded only if NOBODY holds a grant on it and no
            // deploy owns it — each probe failing closed.
            var staticGrant = hub.ServiceProvider.EnumerateStaticNodes()
                .Any(n => n.Path.StartsWith($"{partition}/_Access/", StringComparison.OrdinalIgnoreCase));
            if (staticGrant)
                return Observable.Return<string?>(null);

            var grants = persistence.ListChildPaths($"{partition}/_Access")
                .Take(1)
                .Select(children => children.NodePaths?.Any() == true)
                .Catch<bool, Exception>(ex =>
                {
                    logger?.LogDebug(ex,
                        "[StrandedPartition] could not list '{Partition}/_Access'; treating it as owned",
                        partition);
                    return Observable.Return(true);
                })
                .DefaultIfEmpty(true);
            var synced = persistence.Read(AccessAssignmentGuard.SyncConfigPath(partition), options)
                .Take(1)
                .Select(sync => AccessAssignmentGuard.IsSystemOwned(sync, options))
                .Catch<bool, Exception>(_ => Observable.Return(true))
                .DefaultIfEmpty(false);

            return Observable.Zip(grants, synced, (hasGrants, systemOwned) => hasGrants || systemOwned
                ? null
                : "its root is the bare Space shell the partition bootstrap heals under System, and the "
                  + "partition carries no access grant at all — nobody owns it, so nobody can delete "
                  + "it through its root");
        });
    }

    /// <summary>
    /// Exactly the root <c>HealPartitionRoot</c> writes — <c>Space</c>, named after the partition,
    /// no content, created by System (or unattributed) — and nothing else. A root with content, a
    /// different name, another type or a real creator is somebody's partition.
    /// </summary>
    /// <param name="root">The durable root row.</param>
    /// <param name="partition">The partition it roots.</param>
    internal static bool IsHealedShell(MeshNode root, string partition) =>
        string.Equals(root.NodeType, SpaceNodeType.NodeType, StringComparison.OrdinalIgnoreCase)
        && string.Equals(root.Name, partition, StringComparison.Ordinal)
        && root.Content is null
        && (string.IsNullOrEmpty(root.CreatedBy)
            || string.Equals(root.CreatedBy, WellKnownUsers.System, StringComparison.OrdinalIgnoreCase)
            || string.Equals(root.CreatedBy, WellKnownUsers.Anonymous, StringComparison.OrdinalIgnoreCase));
}
