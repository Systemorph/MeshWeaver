using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// Reconciles every ALREADY-installed package, once, at startup — the REPAIR pass: re-runs the
/// <see cref="IPartitionInstallHook"/>s AND re-asserts the access shape each package's recorded
/// manifest declares (<see cref="PackageInstaller.EnsureDeclaredAccess"/>).
///
/// <para><b>Why a repair pass is needed at all.</b> Hooks now run on install, but every package
/// installed before they existed never ran them, and every account created since inherited the gap.
/// On a live instance that is the entire population: agents shipped by a package are present in the
/// mesh and invisible in every picker. Waiting for the next package update to fix it would leave
/// users broken for however long that takes, for a defect they cannot see or work around.</para>
///
/// <para><b>Why the access re-assert lives here too.</b> The boot install pass
/// (<see cref="InstanceAutoRegistrationService"/>) only revisits the pre-installed baseline and — on
/// a FRESH instance — the operator's default seed; a free package installed any other way (the seed
/// on an earlier boot, the catalog button, an update) is never a candidate again, so a lost policy
/// or grant would stay lost forever. The install records are the one complete inventory, and each
/// record carries the manifest whose declarations drive the shape — so "re-asserted on boot, a lost
/// policy self-heals" holds for EVERY installed package, not just the baseline (#920).</para>
///
/// <para><b>Safe to run every boot.</b> Hooks are required to be idempotent, and the access shape is
/// create-only — so on an already-consistent instance this reads and writes nothing. It is
/// deliberately fire-and-forget and failure-tolerant: repair must never delay or fail startup (the
/// hard failure-propagation contract lives on the install paths themselves).</para>
/// </summary>
/// <param name="hub">Hub supplying the workspace and the registered hooks.</param>
public sealed class InstalledPackageRepairService(IMessageHub hub) : IHostedService
{
    private IDisposable? subscription;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger<InstalledPackageRepairService>();

        subscription = InstalledRecords(logger)
            .SelectMany(records => records.Count == 0
                ? Observable.Return(Unit.Default)
                : records
                    .Select(record => PackageInstaller
                        .EnsureDeclaredAccess(hub, record.Manifest, record.Partition, logger)
                        .Catch<Unit, Exception>(ex =>
                        {
                            logger?.LogWarning(ex,
                                "[PackageRepair] re-asserting declared access for {Partition} failed",
                                record.Partition);
                            return Observable.Return(Unit.Default);
                        })
                        .SelectMany(_ => PackageInstaller.RunInstallHooks(hub, record.Partition, logger)))
                    .ToObservable()
                    .Concat()
                    .DefaultIfEmpty(Unit.Default)
                    .LastAsync()
                    .Do(_ => logger?.LogInformation(
                        "[PackageRepair] reconciled declared access + install hooks for {Count} "
                        + "installed partition(s)", records.Count)))
            .Subscribe(
                _ => { },
                ex => logger?.LogWarning(ex, "[PackageRepair] repair pass failed"));

        return Task.CompletedTask;
    }

    /// <summary>One recorded install: its target partition and the manifest recorded for it.</summary>
    private sealed record InstalledRecord(string Partition, PackageManifest Manifest);

    /// <summary>
    /// Every recorded install, one entry per partition. Reads the <c>Package</c> records the
    /// installer writes, so the repair covers exactly what is installed — no hard-coded package
    /// list. The record's content IS the manifest as installed (id, partition, price, declared
    /// public segments), which is what drives the access re-assert.
    /// </summary>
    private IObservable<IReadOnlyList<InstalledRecord>> InstalledRecords(ILogger? logger) =>
        hub.GetWorkspace()
            .GetQuery("installed-packages-repair",
                $"namespace:{PackageInstaller.InstalledPartition} "
                + $"nodeType:{PackageInstaller.PackageNodeType} select:path,id,name,nodeType,content")
            .Take(1)
            .Timeout(TimeSpan.FromMinutes(2))
            .Select(nodes => (IReadOnlyList<InstalledRecord>)nodes
                .Select(node => (Node: node,
                    Manifest: node.ContentAs<PackageManifest>(hub.JsonSerializerOptions)))
                .Where(x => x.Manifest is not null)
                .Select(x => new InstalledRecord(PartitionOf(x.Node, x.Manifest!), x.Manifest!))
                .Where(r => !string.IsNullOrWhiteSpace(r.Partition))
                .GroupBy(r => r.Partition, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList())
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex, "[PackageRepair] listing installed packages failed");
                return Observable.Return((IReadOnlyList<InstalledRecord>)[]);
            });

    /// <summary>
    /// The target partition of an install record. The record's id IS the package id, and the
    /// installer targets a partition of that name — the recorded manifest's
    /// <see cref="PackageManifest.TargetPartition"/> wins when present so a package whose partition
    /// differs from its id still repairs correctly.
    /// </summary>
    private static string PartitionOf(MeshWeaver.Mesh.MeshNode node, PackageManifest manifest) =>
        string.IsNullOrWhiteSpace(manifest.TargetPartition) ? node.Id : manifest.TargetPartition!;

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        subscription?.Dispose();
        subscription = null;
        return Task.CompletedTask;
    }
}
