using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// Removes a package's INSTALL RECORD when the partition it was installed into is deleted
/// (Systemorph/MeshWeaver#3451).
///
/// <para><b>The dangling reference this closes.</b> An install record lives at
/// <c>{InstalledPartition}/{packageId}</c> — in the <c>Plugins</c> partition, never in the package's
/// own — so deleting the installed partition left the record behind, pointing at nothing. Nothing
/// reconciled it, and <see cref="InstalledPackageRepairService"/> re-drove
/// <see cref="PackageInstaller.EnsureDeclaredAccess"/> at that dead partition on EVERY boot, forever:
/// a permanent per-boot error for every partition anyone had ever deleted, and — before #3451's other
/// half — the writer that re-armed the resurrection race on every restart.</para>
///
/// <para>🚨 <b>Why the discriminator comes from the record side.</b> Core deliberately knows nothing
/// about <c>Plugins/Package</c>, and teaching the delete pipeline about it would be exactly the
/// coupling #3436 spent its fix removing. <see cref="INodePostDeletionHandler"/> is the seam that
/// makes that unnecessary: the plugin catalog registers its OWN post-deletion handler, matching the
/// same STRUCTURAL predicate the partition teardown uses
/// (<see cref="PartitionDefinition.IsPartitionRoot"/>), and answers the one question only it can —
/// "is this partition a recorded install target?". Referential integrity is maintained by the side
/// that owns the reference.</para>
///
/// <para><b>Ordering and blast radius.</b> Post-deletion handlers run at step 5, after the subtree is
/// drained, so "the partition root was deleted" and "the partition is empty" are the same statement
/// by then. Only the RECORD is removed — via <see cref="PackageInstaller.RemoveInstalledRecord"/>,
/// the one sanctioned removal route (<c>Plugins/_Policy</c> caps delete for every ordinary caller,
/// so it runs as System). The record's own delete cannot recurse here: it is namespaced under
/// <c>Plugins</c> and is therefore not a partition root. Deleting the <c>Plugins</c> partition itself
/// is excluded — its records go with it, and cascading each one first would be a no-op race against
/// the drain that just removed them.</para>
///
/// <para>Failures are logged and surfaced as a Warning on the deletion activity; they never
/// un-delete the partition. 100% reactive — no <c>async</c>/<c>await</c>.</para>
/// </summary>
/// <param name="hub">Hub supplying the workspace, mesh service and access service.</param>
/// <param name="logger">Diagnostics.</param>
public sealed class InstallRecordPartitionTeardownHandler(
    IMessageHub hub,
    ILogger<InstallRecordPartitionTeardownHandler>? logger = null) : INodePostDeletionHandler
{
    /// <summary>
    /// Diagnostic label only — <see cref="Matches"/> never reads it, exactly as
    /// <c>PartitionDropPostDeletionHandler</c> does for the same structural reason.
    /// </summary>
    public string NodeType => "*";

    /// <summary>
    /// Structural match: every partition ROOT except the install-RECORDS partition itself and the
    /// system-managed mirrors. No NodeType is consulted, so a partition rooted at an in-mesh type
    /// that arrived after boot (<c>Store/Plugin</c>, <c>Crm/Client</c>) is covered by construction —
    /// which matters here more than anywhere, because those ARE the installed partitions.
    /// </summary>
    /// <param name="deletedNode">The node as it was loaded before deletion.</param>
    /// <returns><c>true</c> when the deleted node is a partition root whose install record may exist.</returns>
    public bool Matches(MeshNode deletedNode) =>
        PartitionDefinition.IsPartitionRoot(deletedNode)
        && !WellKnownPartitions.IsMirror(deletedNode.Id)
        && !string.Equals(deletedNode.Id, PackageInstaller.InstalledPartition,
            StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public IObservable<Unit> Handle(MeshNode deletedNode, string? deletedBy)
    {
        // Defence in depth: Handle is public, and a caller invoking it directly must never remove a
        // record because a NESTED node was deleted.
        if (!Matches(deletedNode))
            return Observable.Return(Unit.Default);

        var partition = deletedNode.Id;
        return RecordedInstallsTargeting(hub, partition)
            .SelectMany(packageIds => packageIds.Count == 0
                ? Observable.Return(Unit.Default)
                : packageIds
                    .Select(packageId => PackageInstaller
                        .RemoveInstalledRecord(hub, packageId, logger)
                        .Do(_ => logger?.LogInformation(
                            "[PluginCatalog] removed the install record for '{PackageId}': its target "
                            + "partition '{Partition}' was deleted by {User}",
                            packageId, partition, deletedBy ?? "system"))
                        .Catch<bool, Exception>(ex =>
                        {
                            logger?.LogWarning(ex,
                                "[PluginCatalog] could not remove the install record for '{PackageId}' "
                                + "after its partition '{Partition}' was deleted — it will keep "
                                + "re-asserting access into a partition that is gone until it is "
                                + "removed from the admin orphan list",
                                packageId, partition);
                            return Observable.Return(false);
                        }))
                    .ToObservable()
                    .Concat()
                    .TakeLast(1)
                    .Select(_ => Unit.Default));
    }

    /// <summary>
    /// The package ids whose recorded install targets <paramref name="partition"/>, from TWO sources
    /// unioned — deliberately, because neither alone is both authoritative and complete:
    /// <list type="number">
    ///   <item>an authoritative point read of <c>{InstalledPartition}/{partition}</c> through the
    ///     storage adapter. The installer names a record after the package id and targets a partition
    ///     of that name, so this is the shape of nearly every install — and it is a STORE read, not a
    ///     query, so a record written seconds ago is already visible (CQRS lag cannot hide it);</item>
    ///   <item>a listing of the records partition — the same query
    ///     <see cref="InstalledPackageRepairService"/> issues — which is the only way to see a record
    ///     whose <see cref="PackageManifest.TargetPartition"/> differs from its id. A listing is the
    ///     sanctioned CQRS use, and a stale answer here can only MISS a record (which the boot
    ///     reconciliation then reports), never remove one it should not.</item>
    /// </list>
    ///
    /// <para>🚨 No <c>.Catch</c> on the listing, on purpose: a listing that could not be read is not
    /// evidence that there is no record. The fault propagates, lands as a Warning on the deletion
    /// activity, and the record stays visible to the boot reconciliation — swallowing it would report
    /// a clean sweep having checked nothing.</para>
    /// </summary>
    internal static IObservable<IReadOnlyList<string>> RecordedInstallsTargeting(
        IMessageHub hub, string partition)
    {
        var options = hub.JsonSerializerOptions;
        var storage = hub.ServiceProvider.GetService<IStorageAdapter>();
        var byName = storage is null
            ? Observable.Return<string?>(null)
            : storage.Read($"{PackageInstaller.InstalledPartition}/{partition}", options)
                .Take(1)
                .DefaultIfEmpty(null!)
                .Select(node => node?.ContentAs<PackageManifest>(options) is { } manifest
                                && string.Equals(
                                    PackageInstaller.TargetPartitionOf(node!.Id, manifest), partition,
                                    StringComparison.OrdinalIgnoreCase)
                    ? node!.Id
                    : null)
                .Catch<string?, Exception>(_ => Observable.Return<string?>(null));

        var byListing = hub.GetWorkspace()
            .GetQuery("installed-packages-partition-teardown",
                $"namespace:{PackageInstaller.InstalledPartition} "
                + $"nodeType:{PackageInstaller.PackageNodeType} select:path,id,name,nodeType,content")
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(30))
            .Select(nodes => nodes
                .Select(node => (node.Id, Manifest: node.ContentAs<PackageManifest>(options)))
                .Where(x => x.Manifest is not null
                            && string.Equals(
                                PackageInstaller.TargetPartitionOf(x.Id, x.Manifest!), partition,
                                StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Id))
            // 🚨 A listing that COMPLETES WITHOUT EMITTING is not caught by the Timeout above —
            // Timeout faults on silence, not on a clean finish (#2901) — and a Zip leg that never
            // emits would park the whole deletion's step 5. Empty means "no records seen", which is
            // the same conclusion as an empty listing.
            .DefaultIfEmpty(Enumerable.Empty<string>());

        return Observable.Zip(byName, byListing, (direct, listed) => (IReadOnlyList<string>)listed
            .Concat(direct is null ? [] : new[] { direct })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList());
    }
}
