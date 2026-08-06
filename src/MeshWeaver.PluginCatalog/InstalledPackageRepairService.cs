using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// Re-runs the <see cref="IPartitionInstallHook"/>s for every ALREADY-installed package, once, at
/// startup — the REPAIR pass.
///
/// <para><b>Why a repair pass is needed at all.</b> Hooks now run on install, but every package
/// installed before they existed never ran them, and every account created since inherited the gap.
/// On a live instance that is the entire population: agents shipped by a package are present in the
/// mesh and invisible in every picker. Waiting for the next package update to fix it would leave
/// users broken for however long that takes, for a defect they cannot see or work around.</para>
///
/// <para><b>Safe to run every boot.</b> Hooks are required to be idempotent, and the AI hook writes
/// only when a user's sources actually change — so on an already-repaired instance this reads and
/// writes nothing. It is deliberately fire-and-forget and failure-tolerant: repair must never delay
/// or fail startup.</para>
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

        // Nothing to repair when no hook is registered — skip the query entirely.
        if (!hub.ServiceProvider.GetServices<IPartitionInstallHook>().Any())
            return Task.CompletedTask;

        subscription = InstalledPartitions(logger)
            .SelectMany(partitions => partitions.Count == 0
                ? Observable.Return(Unit.Default)
                : partitions
                    .Select(partition => PackageInstaller.RunInstallHooks(hub, partition, logger))
                    .ToObservable()
                    .Concat()
                    .DefaultIfEmpty(Unit.Default)
                    .LastAsync()
                    .Do(_ => logger?.LogInformation(
                        "[PackageRepair] reconciled install hooks for {Count} installed partition(s)",
                        partitions.Count)))
            .Subscribe(
                _ => { },
                ex => logger?.LogWarning(ex, "[PackageRepair] repair pass failed"));

        return Task.CompletedTask;
    }

    /// <summary>
    /// The partitions of every recorded install. Reads the <c>Package</c> records the installer
    /// writes, so the repair covers exactly what is installed — no hard-coded package list.
    /// </summary>
    private IObservable<IReadOnlyList<string>> InstalledPartitions(ILogger? logger) =>
        hub.GetWorkspace()
            .GetQuery("installed-packages-repair",
                $"namespace:{PackageInstaller.InstalledPartition} "
                + $"nodeType:{PackageInstaller.PackageNodeType} select:path,id,name,nodeType,content")
            .Take(1)
            .Timeout(TimeSpan.FromMinutes(2))
            .Select(nodes => (IReadOnlyList<string>)nodes
                .Select(PartitionOf)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList())
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex, "[PackageRepair] listing installed packages failed");
                return Observable.Return((IReadOnlyList<string>)[]);
            });

    /// <summary>
    /// The target partition of an install record. The record's id IS the package id, and the
    /// installer targets a partition of that name — read from content when present so a package
    /// whose partition differs from its id still repairs correctly.
    /// </summary>
    private string? PartitionOf(MeshWeaver.Mesh.MeshNode node)
    {
        if (node.Content is System.Text.Json.JsonElement json
            && json.ValueKind == System.Text.Json.JsonValueKind.Object
            && json.TryGetProperty("targetPartition", out var value)
            && value.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var declared = value.GetString();
            if (!string.IsNullOrWhiteSpace(declared))
                return declared;
        }
        return node.Id;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        subscription?.Dispose();
        subscription = null;
        return Task.CompletedTask;
    }
}
