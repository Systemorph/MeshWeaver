using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Security;

/// <summary>
/// Finishes the teardown of a partition that NO ordinary delete can reach any more, when its
/// RECORD — the <c>Admin/Partition/{partition}</c> <see cref="PartitionDefinition"/> node — is
/// deleted directly. The verb <see href="https://github.com/Systemorph/MeshWeaver/issues/5073">#5073</see>'s
/// second defect was missing. A DELETE VALIDATOR, so the drop runs BEFORE the record goes and a
/// drop that fails REFUSES the delete: the record stays, and with it the only handle by which the
/// teardown can be retried. That is the ordinary teardown's own ordering (store drop first,
/// definition delete second, "so the partition remains visible for a retry"), and it is why this
/// is not an <see cref="INodePostDeletionHandler"/> — a post-deletion handler runs inside the
/// record's own deletion scope, where the storage write guard refuses to write the record back.
/// The creation-side mirror, <see cref="OwnsPartitionProvisioningValidator"/>, provisions the
/// schema from a validator for the same reason.
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
/// schema. Stranded is a CONJUNCTION — a root nobody can reach the partition through, AND nobody
/// who owns it:</para>
/// <list type="number">
///   <item><description><b>The root</b> is either absent — no durable row and no static root
///     serves the partition path, the orphan as the pre-#3436 delete left it — or exactly the
///     shell <c>HealPartitionRoot</c> writes (<c>Space</c>, named after the partition, no content,
///     created by System), which can only have got there by the bootstrap re-rooting an
///     orphan.</description></item>
///   <item><description><b>And nobody owns it</b>: no access grant under <c>{partition}/_Access</c>,
///     durable or static, and no GitSync configuration (a synced partition is owned by the
///     deploy). Asked for BOTH root shapes, because the recursive delete that orphaned a partition
///     enumerated <c>mesh_nodes</c> descendants only — its <c>_Access</c> rows live in a satellite
///     table the fan-out never visits, so a rootless partition can still carry every grant it ever
///     had, and its grant holders still reach its nodes through the synthesized placeholder root
///     and re-root it on their next write. Such a partition is somebody's.</description></item>
/// </list>
/// <para>Anything else — a real root, an owner, a synced partition, a static partition — is NOT
/// stranded: the record delete proceeds (it is the admin's record to delete) but the store is NOT
/// touched, and it says so at Warning, because a live partition's record disappearing is already a
/// defect worth a line (it leaves the partition out of every listing and the routing prime). Every
/// probe fails CLOSED: an unreadable root or grant listing reads as <i>owned</i>, and nothing is
/// dropped.</para>
///
/// <para><b>The claim comes first.</b> The verdict is taken INSIDE the partition's deletion scope
/// (<see cref="RecentlyDeletedRegistry.BeginSubtreeDeletion"/>), so no recreate can land between
/// the reading and the drop and then be dropped on a verdict about a partition that no longer
/// exists — the #3451 shape. A root delete that starts AFTER the claim is the one residue: two
/// idempotent drops of one schema, serialised by the provider's own lock.</para>
///
/// <para><b>Not a second drop.</b> The ordinary teardown deletes this same record as its second
/// step, inside its own deletion scope; this validator reads the tombstone first and stands down
/// while a partition's deletion is in flight or on record. And only a DIRECT delete of the record
/// counts: a record removed as part of a cascade (deleting <c>Admin/Partition</c> or <c>Admin</c>
/// recursively) is bookkeeping being removed, not a request to drop every stranded schema at
/// once. 100% reactive; the async DDL edge stays sealed in each provider's <c>IIoPool</c>.</para>
/// </summary>
public sealed class StrandedPartitionTeardownValidator : INodeValidator
{
    private readonly IMessageHub hub;
    private readonly ILogger<StrandedPartitionTeardownValidator>? logger;

    /// <summary>
    /// Creates the validator. It matches the direct deletion of an <c>Admin/Partition/{partition}</c>
    /// record and drops the partition's store only when that partition is stranded.
    /// </summary>
    /// <param name="hub">Hub supplying the storage providers, the storage adapter and the tombstone registry.</param>
    /// <param name="logger">Diagnostics.</param>
    public StrandedPartitionTeardownValidator(
        IMessageHub hub,
        ILogger<StrandedPartitionTeardownValidator>? logger = null)
    {
        this.hub = hub;
        this.logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<NodeOperation> SupportedOperations => [NodeOperation.Delete];

    /// <summary>
    /// A <see cref="PartitionDefinition"/> record under <c>Admin/Partition</c> whose id is a valid,
    /// non-mirror partition segment. The mirrors (<c>Auth</c>, <c>User</c>) are populated by a
    /// database trigger and have no root by design — deleting their record must never drop them.
    /// </summary>
    /// <param name="node">The node being deleted.</param>
    /// <returns><c>true</c> when the node is a partition record this validator may act on.</returns>
    public static bool IsPartitionRecord(MeshNode node) =>
        string.Equals(node.NodeType, PartitionNodeType.NodeType, StringComparison.OrdinalIgnoreCase)
        && string.Equals(node.Namespace, PartitionNodeType.Namespace, StringComparison.OrdinalIgnoreCase)
        && PartitionDefinition.IsValidPartitionSegment(node.Id)
        && !WellKnownPartitions.IsMirror(node.Id);

    /// <inheritdoc />
    public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
    {
        // A DIRECT delete carries its own path as the cascade root; a record removed as a leaf of
        // a wider recursive delete carries that delete's root instead (DeleteNodeRequest.CascadeRootPath).
        if (context.Operation != NodeOperation.Delete
            || !IsPartitionRecord(context.Node)
            || !string.Equals(context.DeleteCascadeRootPath, context.Node.Path, StringComparison.OrdinalIgnoreCase))
            return Observable.Return(NodeValidationResult.Valid());

        var partition = context.Node.Id;
        var deletedBy = context.AccessContext?.ObjectId ?? "system";
        var registry = hub.ServiceProvider.GetRequiredService<RecentlyDeletedRegistry>();

        // The ordinary teardown deletes this record as its own second step, inside its deletion
        // scope — the tombstone says so, and this validator stands down rather than dropping a
        // store the root delete is already dropping.
        if (registry.IsUnderActiveDeletion(partition, out var deletionRoot))
        {
            logger?.LogDebug(
                "[StrandedPartition] record of '{Partition}' deleted while its deletion is in flight "
                + "(subtree '{Root}') — the ordinary teardown owns the drop; nothing to do here",
                partition, deletionRoot);
            return Observable.Return(NodeValidationResult.Valid());
        }
        if (registry.IsRecentlyDeleted(partition))
        {
            logger?.LogDebug(
                "[StrandedPartition] record of '{Partition}' deleted right after the partition itself "
                + "was — the ordinary teardown owns the drop; nothing to do here",
                partition);
            return Observable.Return(NodeValidationResult.Valid());
        }

        // 🚨 CLAIM FIRST, CLASSIFY UNDER THE CLAIM (review on #5203). The verdict below is a
        // reading taken at one instant and acted on at another — the exact shape #3451 was. With
        // the deletion scope open, SubtreeDeletionGuardStorageAdapter refuses every write at or
        // under the partition root, so a recreate (an installer provisioning the same name, a
        // child write healing a root) cannot land between the reading and the drop and then be
        // dropped on a verdict about a partition that no longer exists.
        return Observable.Using(
            () => registry.BeginSubtreeDeletion(partition),
            _ => StrandedBecause(partition).SelectMany(reason =>
            {
                if (reason is null)
                {
                    logger?.LogWarning(
                        "[StrandedPartition] the record 'Admin/Partition/{Partition}' is being deleted by "
                        + "{User} but the partition is NOT stranded — it has a root somebody authored, an "
                        + "access grant, a GitSync configuration, or is served by configuration — so its "
                        + "backing store is NOT dropped. The partition will be missing from partition "
                        + "listings and the routing prime; its owner tears it down through its root "
                        + "(a rootless partition re-roots on the owner's next write), or its stale "
                        + "grants are removed first and the record deleted again.",
                        partition, deletedBy);
                    return Observable.Return(NodeValidationResult.Valid());
                }

                logger?.LogWarning(
                    "[StrandedPartition] finishing the teardown of '{Partition}' on the deletion of its "
                    + "record by {User}: {Reason}. No ordinary delete could reach it — dropping its "
                    + "backing store on every provider and evicting its cached queries (#5073).",
                    partition, deletedBy, reason);

                // The same tombstone the ordinary teardown holds across its drop (#3451), so the
                // guard refuses a write racing the DDL the way it refuses one racing a root delete.
                registry.MarkDeleted(partition);
                return PartitionDropPostDeletionHandler
                    .DropStores(hub, partition, $"the deletion of its record by {deletedBy} ({reason})", logger)
                    .Do(__ => PartitionDropPostDeletionHandler.DropCachedPartitionQueries(hub, partition, logger))
                    .Select(__ => NodeValidationResult.Valid())
                    // 🚨 A DROP THAT FAULTS REFUSES THE DELETE (review on #5203). The record is the
                    // only handle by which this teardown can be retried; removing it over a store
                    // that is still there would strand the partition twice over. The tombstone is
                    // lifted — the partition is still there — and the reason travels on the
                    // refusal, with the exception in the log.
                    .Catch<NodeValidationResult, Exception>(ex =>
                    {
                        registry.Clear(partition);
                        logger?.LogError(ex,
                            "[StrandedPartition] the store drop for '{Partition}' FAILED — the record "
                            + "'Admin/Partition/{Partition}' is kept so the teardown can be retried by "
                            + "deleting it again.",
                            partition, partition);
                        return Observable.Return(NodeValidationResult.Invalid(
                            $"the backing store of the stranded partition '{partition}' could not be "
                            + $"dropped ({ex.GetType().Name}: {ex.Message}); its record is kept so the "
                            + "teardown can be retried by deleting it again",
                            NodeRejectionReason.ValidationFailed));
                    });
            }))
            .DefaultIfEmpty(NodeValidationResult.Valid());
    }

    /// <summary>
    /// Why <paramref name="partition"/> is stranded — the conjunction the class remark names — or
    /// <c>null</c> when it is somebody's. Emits exactly once; every probe that cannot answer reads
    /// as OWNED, so a fault never drops a store.
    /// </summary>
    /// <param name="partition">The partition whose record is being deleted.</param>
    internal IObservable<string?> StrandedBecause(string partition)
    {
        // A partition served by configuration is not data: its root (or its own record) is a
        // static node, and there is nothing in a store for this validator to finish.
        if (hub.ServiceProvider.FindStaticNode(partition) is { IsDefinitionOnly: false })
            return Observable.Return<string?>(null);

        var persistence = hub.ServiceProvider.GetService<IStorageAdapter>();
        if (persistence is null)
            return Observable.Return<string?>(null);
        var options = hub.JsonSerializerOptions;

        // Fail CLOSED on the root read: an unreadable store is somebody's partition for this
        // purpose. `Read` is the same authoritative read EnsurePartitionBootstrap takes before it
        // heals.
        var root = persistence.Read(partition, options)
            .Take(1)
            .Select(n => (Node: n, Readable: true))
            .Catch<(MeshNode? Node, bool Readable), Exception>(ex =>
            {
                logger?.LogDebug(ex,
                    "[StrandedPartition] could not read the root of '{Partition}'; treating it as owned",
                    partition);
                return Observable.Return<(MeshNode? Node, bool Readable)>((null, false));
            })
            .DefaultIfEmpty((null, true));

        return root.SelectMany(r =>
        {
            if (!r.Readable)
                return Observable.Return<string?>(null);
            // The root decides only whether anyone can REACH the partition through it; it does
            // not decide who owns it.
            var rootShape = r.Node is null
                ? "its root node is gone and no static root serves it — the shape a partition-root "
                  + "delete left behind before the structural teardown existed"
                : IsHealedShell(r.Node, partition)
                    ? "its root is the bare Space shell the partition bootstrap heals under System"
                    : null;
            if (rootShape is null)
                return Observable.Return<string?>(null);

            // 🚨 OWNERSHIP IS ASKED FOR BOTH SHAPES (review on #5203) — see the class remark. Each
            // probe fails closed.
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
                : rootShape + ", and the partition carries no access grant and no GitSync configuration "
                  + "— nobody owns it, so nobody can tear it down through its root");
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
