using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Graph;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// 🚨 The plugin catalog's <see cref="IBootBundleSource"/>: before the boot sweep judges anything,
/// offer the DECLARED registries' prebuilt NodeType bundles for every package this installation has
/// already installed in a partition this mesh does not track.
///
/// <para><b>Why exactly that set.</b> It is the set the compile watcher's untracked-module gate
/// (MeshWeaver#3583) refuses to compile here: a module's content in a partition no sync source
/// tracks may only be SERVED from a bundle. Every other package compiles from its tracked source
/// as before, so this pass touches nothing on an instance that syncs its partitions, nothing on a
/// mesh with no tracking seam (no gate fires there), and nothing on a first boot (no install
/// record yet — the default install adopts as it always did).</para>
///
/// <para><b>Same adoption, earlier.</b> Each package goes through <see cref="PluginBundleClient.Adopt"/>
/// — the download and the per-assembly gates of the default install, unchanged (framework identity,
/// platform range, source fingerprint). Only WHEN changes: before the sweep, so its provisional
/// adoption is held by the publication gate and counted by this process's own ledger, instead of
/// after a sweep that has already refused readiness on these very types and so refuses the stamp.
/// See <see cref="IBootBundleSource"/> for the deadlock this ends.</para>
///
/// <para>Bounded per package by the reconciler's own adoption budget, sequential, and NEVER faults:
/// a registry that cannot be reached adopts nothing, says so, and the sweep judges as before.</para>
/// </summary>
public sealed class RegistryBundlesBeforeSweep : IBootBundleSource
{
    /// <summary>How long the install-record listing may take before this pass adopts nothing.</summary>
    internal static readonly TimeSpan ListingBudget = TimeSpan.FromSeconds(30);

    private readonly IMessageHub hub;
    private readonly ILogger<RegistryBundlesBeforeSweep>? logger;

    /// <summary>Creates the source over the mesh hub.</summary>
    /// <param name="hub">The mesh hub — resolves options, tokens, tracking and the query service.</param>
    public RegistryBundlesBeforeSweep(IMessageHub hub)
    {
        this.hub = hub;
        logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger<RegistryBundlesBeforeSweep>();
    }

    /// <inheritdoc />
    public string Name => "plugin registries (installed packages in untracked partitions)";

    /// <summary>The package's partition: the install record's target, else its id. Pure.</summary>
    /// <param name="record">The install record's manifest.</param>
    internal static string PartitionOf(PackageManifest record)
        => string.IsNullOrWhiteSpace(record.TargetPartition) ? record.Id : record.TargetPartition.Trim().Trim('/');

    /// <inheritdoc />
    public IObservable<int> SeedBeforeSweep()
        => Observable.Defer(() =>
        {
            var services = hub.ServiceProvider;
            var tracking = services.GetServices<IPartitionSourceTracking>().ToArray();
            var options = services.GetService<PluginCatalogOptions>() ?? new PluginCatalogOptions();
            var registries = RegistryTokenResolver.WithLegacyTokens(options, options.EffectiveRegistries);
            var tokens = services.GetService<RegistryTokenResolver>();
            var mesh = services.GetService<IMeshService>();
            var access = services.GetService<AccessService>();
            // Nothing to offer, or nothing that would park without it — the sweep is unchanged.
            if (tracking.Length == 0 || registries.Count == 0 || tokens is null || mesh is null || access is null)
                return Observable.Return(0);

            return access
                .RunAsSystem(() => mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(
                        $"path:{PackageInstaller.InstalledPartition} scope:children nodeType:{PackageInstaller.PackageNodeType}"))
                    .Take(1))
                .Timeout(ListingBudget)
                .Select(change => change.Items
                    .Select(n => n.ContentAs<PackageManifest>(hub.JsonSerializerOptions))
                    .OfType<PackageManifest>()
                    .Where(m => !string.IsNullOrWhiteSpace(m.Id))
                    .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToImmutableArray())
                .SelectMany(records => UntrackedOnly(records, tracking))
                .SelectMany(packages =>
                {
                    if (packages.IsEmpty)
                        return Observable.Return(0);
                    logger?.LogInformation(
                        "Boot bundle adoption: offering the declared registries' bundles for {Count} installed "
                        + "package(s) in partitions this mesh does not track, BEFORE the sweep judges them: {Packages}",
                        packages.Length, string.Join(", ", packages));
                    // ONE client per registry for the whole pass: the bundle index is read once.
                    var clients = registries
                        .Select(r => PluginBundleClient.WithCredential(hub, r.Url, tokens.Credential(r)))
                        .ToArray();
                    return packages
                        .Select(package => clients
                            .Select(client => AdoptOne(client, package))
                            .Concat()
                            .Sum())
                        .Concat()
                        .Sum();
                })
                .Take(1)
                .DefaultIfEmpty(0)
                .Catch<int, Exception>(ex =>
                {
                    logger?.LogWarning(ex,
                        "Boot bundle adoption: the installed packages could not be listed — nothing is "
                        + "offered before the sweep; the default install still adopts after it");
                    return Observable.Return(0);
                });
        });

    private IObservable<int> AdoptOne(PluginBundleClient client, string package)
        => client.Adopt(package)
            .Take(1)
            .DefaultIfEmpty(0)
            .Timeout(RegistryUpdateReconciler.PerPackageAdoptBudget)
            .Catch<int, Exception>(ex =>
            {
                // Never fails the warm-up. A require-prebuilt refusal included: the default install
                // re-offers the package after the bake and names it there, in its own summary.
                logger?.LogWarning(ex,
                    "Boot bundle adoption: {Package} adopted nothing before the sweep ({Reason}) — its "
                    + "types are judged as if no registry had been asked", package, ex.GetType().Name);
                return Observable.Return(0);
            });

    /// <summary>How long the WHOLE tracking-read stage may take, every install record together.
    /// The reads run in parallel under this one bound, so a hung provider costs boot this much once,
    /// never once per installed package.</summary>
    internal static readonly TimeSpan TrackingBudget = TimeSpan.FromSeconds(30);

    /// <summary>The ids of the packages whose partition no tracking provider tracks. The records are
    /// asked in PARALLEL under ONE overall budget (<see cref="TrackingBudget"/>); a record whose answer
    /// did not arrive inside it, or could not be read, reads TRACKED — the gate's own rule — so its
    /// package is left to compile as before. Never faults.</summary>
    private IObservable<ImmutableArray<string>> UntrackedOnly(
        ImmutableArray<PackageManifest> records, IPartitionSourceTracking[] tracking)
    {
        if (records.IsEmpty)
            return Observable.Return(ImmutableArray<string>.Empty);
        return records
            .Select(record => tracking
                .Select(t => t.IsTracked(PartitionOf(record)).Take(1))
                .CombineLatest()
                .Select(answers => answers.Any(tracked => tracked))
                .Take(1)
                .Catch<bool, Exception>(ex =>
                {
                    logger?.LogWarning(ex,
                        "Boot bundle adoption: whether partition '{Partition}' tracks a source could not be "
                        + "read — read as tracked, so {Package} is not offered here", PartitionOf(record), record.Id);
                    return Observable.Return(true);
                })
                .Select(tracked => (record.Id, Tracked: tracked)))
            .Merge()
            // ONE bound over the whole stage: what answered in time is kept, the rest reads tracked.
            .TakeUntil(Observable.Timer(TrackingBudget))
            .ToList()
            .Do(answers =>
            {
                if (answers.Count < records.Length)
                    logger?.LogWarning(
                        "Boot bundle adoption: {Missing} of {Total} install record(s) got no tracking answer "
                        + "within {Budget}s — read as tracked, so those packages are not offered before the sweep",
                        records.Length - answers.Count, records.Length, TrackingBudget.TotalSeconds);
            })
            .Select(answers => answers
                .Where(answer => !answer.Tracked)
                .Select(answer => answer.Id)
                .ToImmutableArray())
            .Catch<ImmutableArray<string>, Exception>(ex =>
            {
                logger?.LogWarning(ex,
                    "Boot bundle adoption: the tracking read failed — nothing is offered before the sweep");
                return Observable.Return(ImmutableArray<string>.Empty);
            });
    }
}
