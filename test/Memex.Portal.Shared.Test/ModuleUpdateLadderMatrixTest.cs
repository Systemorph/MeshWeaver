#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.SelfUpdate;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.SelfUpdate;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using PackagingManifest = MeshWeaver.Plugin.Packaging.PluginManifest;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>The module update ladder as an ORDERED MATRIX</b> (<c>Doc/Architecture/ModuleUpdateLadder</c>
/// → "The matrix"; policies <c>platform-backwards-compatibility</c>, <c>packages-auto-update</c>,
/// <c>module-live-update-default</c>, <c>package-min-mesh-version</c>). Platform and modules move
/// INDEPENDENTLY, one side per step: <c>P1+M1 → P2+M1 → P2+M2 → P2+M3 → P3+M3</c>. Each row is
/// (running platform, the module set the registry publishes) together with what must hold after it:
/// every module's loaded version, how each one activated (live / restart / none / declined) and
/// whether a platform roll happened. Each instance walks its own table as ONE ordered scenario.
///
/// <para><b>Two modules with different floors.</b> <c>M_a</c> swaps live. <c>M_b</c> declares boot-time
/// infrastructure, so it activates by exactly one restart. The <b>ordinary</b> instance follows its
/// policy and rolls a promoted platform one step after the <b>control</b> instance. Control always
/// runs the newest platform, so a module whose floor is the newest platform lands there first
/// (row B3).</para>
///
/// <para><b>The premise of row E1 holds for every step.</b> No seal exists for the running identity, no
/// platform build is consulted, and each module's partition is SYNC-OWNED with its sync still at
/// old content. The only inputs are what the registry publishes and the running platform.</para>
///
/// <para><b>What is real and what is a seam.</b> Real: the registry reconcile
/// (<see cref="RegistryUpdateReconciler.ReconcileNow"/>), the bundle client and its floor decision,
/// <see cref="ModuleLandingService"/>, the module-set proposal, the <see cref="ModuleReload"/> request
/// and its executor, and the self-update restart path (<see cref="SelfUpdateHostedService"/> with a
/// recording Kubernetes updater). A platform roll is modelled the way it happens: the running
/// version changes (the sanctioned <see cref="RunningPlatformVersionOverride"/> seam), and the
/// process boots the same module volume through the production boot computation
/// (<see cref="ModuleActivationBoot.ComputeEffectiveModuleEntriesForPlatform"/>). The live swap goes
/// through <see cref="IModuleLiveActivation"/>, which is the reload's own call site. Here a test loader
/// answers it, and the real loader is #6123's. A restart's new process reports through a
/// <see cref="ModuleReloadAgent"/> started after the restart was stamped, exactly as
/// <c>ModuleReloadByRestartTest</c> does.</para>
///
/// <para><b>Negative controls.</b> <see cref="APinnedPackage_FailsTheMatrixAtTheFirstModuleStep"/>
/// runs the ordinary table with <c>M_a</c> pinned. This is a real production hold. The walk must
/// report a failure at step 2 and at no earlier step, which proves the matrix fails at the exact step
/// where a module stops moving. A by-hand control was also run and its result is in the pull request:
/// it re-inserts the retired coupling, "a sync-owned module waits for the seal its content does".</para>
/// </summary>
public abstract class ModuleUpdateLadderScenario(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected const string RegistryHost = "registry.ladder.test";
    protected const string RegistryUrl = "https://" + RegistryHost;
    protected const string Token = "mwi_ladder_test";

    /// <summary>Three builds of ONE compatibility key, ordered by their run ordinal.</summary>
    public const string P1 = "3.0.0-ci.9400";
    public const string P2 = "3.0.0-ci.9410";
    public const string P3 = "3.0.0-ci.9420";

    /// <summary>The live-swappable module.</summary>
    public static readonly LadderModule Ma = new("LadderA", "MeshWeaver.LadderA", BootTime: false);

    /// <summary>The module that declares boot-time infrastructure — one restart.</summary>
    public static readonly LadderModule Mb = new("LadderB", "MeshWeaver.LadderB", BootTime: true);

    public static readonly ImmutableList<LadderModule> Modules = [Ma, Mb];

    private readonly string landingRoot = Path.Combine(Path.GetTempPath(), "mw-ladder-" + Guid.NewGuid().ToString("N"));
    private string platform = P1;
    private ImmutableDictionary<string, (string Directory, string Version)> loaded =
        ImmutableDictionary<string, (string, string)>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);

    protected LadderRegistry Registry { get; } = new();
    protected RecordingUpdater Updater { get; } = new();
    protected LadderLoader Loader => LoaderInstance;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        Directory.CreateDirectory(landingRoot);
        return base.ConfigureMesh(builder)
            .AddGitHubSyncTypes()
            .AddPluginCatalog()
            .ConfigureServices(services =>
            {
                services.AddGitHubSyncServices();
                services
                    .AddSingleton<IHttpClientFactory>(new HostRoutingClientFactory(Registry))
                    // The running platform is READ on every decision, so a roll is visible to the
                    // next reconcile exactly as a new process would see it.
                    .AddTransient(_ => new RunningPlatformVersionOverride(Volatile.Read(ref platform)))
                    .AddSingleton(new ModuleLandingService(baseDirectory: landingRoot))
                    .AddSingleton(new PluginCatalogOptions
                    {
                        Registries = [new PluginRegistryReference { Name = "ladder", Url = RegistryUrl, Token = Token }],
                    })
                    .AddSingleton(new ModuleReloadAgent
                    {
                        LoadedGenerations = () => loaded.ToImmutableDictionary(kv => kv.Key, kv => kv.Value.Directory),
                        LoadedNames = () => loaded.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase),
                    })
                    .AddSingleton<IModuleActivationRestart>(sp => new SelfUpdateHostedService(
                        sp.GetRequiredService<MeshWeaver.Messaging.IMessageHub>(), new NoTags(), Updater, new SelfUpdateOptions()))
                    .AddSingleton<IModuleLiveActivation>(LoaderInstance);
                return services;
            });
    }

    private LadderLoader? loaderInstance;

    private LadderLoader LoaderInstance => loaderInstance ??= new LadderLoader(this);

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try
        {
            if (Directory.Exists(landingRoot))
                Directory.Delete(landingRoot, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory that outlives the test is harmless.
        }
    }

    protected AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private RegistryUpdateReconciler Reconciler => Mesh.ServiceProvider.GetRequiredService<RegistryUpdateReconciler>();

    protected string Platform => Volatile.Read(ref platform);

    private ModuleActivationEntry? Head(LadderModule module) =>
        ModuleActivationSidecar.Read(landingRoot).Entries.SingleOrDefault(e => e.Name == module.Name);

    // ───────────────────────────────────────────────────────────── the process seams

    /// <summary>A live swap: this process now serves the generation the activation record names.</summary>
    internal void SwapIn(string module)
    {
        var head = ModuleActivationSidecar.Read(landingRoot).Entries.Single(e => e.Name == module);
        ImmutableInterlocked.Update(ref loaded, l => l.SetItem(module, (head.Directory!, head.Version!)));
    }

    /// <summary>A process booting the module volume on the running platform: the production boot
    /// computation decides which generation each module serves.</summary>
    private ImmutableDictionary<string, (string Directory, string Version)> Boot(Action<string, string>? onSkipped = null)
    {
        var running = Platform;
        // Store-only modules: the image ships no copy, so the image-copy rule (#6103) has nothing
        // to decide and the platform overload is the whole boot decision for them.
        var effective = ModuleActivationBoot.ComputeEffectiveModuleEntriesForPlatform(
            baselineEntries: [],
            ModuleActivationSidecar.Read(landingRoot),
            floor => ModulePlatformFloor.DeclineReason(floor, running),
            entry => ModuleActivationBoot.LandedModuleDllExists(landingRoot, entry),
            onSkipped,
            onAdvisory: null,
            platformIdentities: null);
        return effective
            .Where(m => m.Landed is { Directory: not null, Version: not null })
            .ToImmutableDictionary(m => m.Landed!.Name, m => (m.Landed!.Directory!, m.Landed!.Version!), StringComparer.OrdinalIgnoreCase);
    }

    // ───────────────────────────────────────────────────────────── the walk

    /// <summary>
    /// Walks <paramref name="rows"/> in order on THIS instance. It returns the first step whose
    /// expectation does not hold (with what was measured), or a verdict with no failure. It never
    /// throws on a mismatch, so a negative control can assert WHERE the walk failed.
    /// </summary>
    protected async Task<LadderVerdict> Walk(
        ImmutableList<LadderRow> rows, CancellationToken ct,
        Func<PackageManifest, PackageManifest>? shapeRecord = null, bool requireNamedDecline = false)
    {
        var seenRequests = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase);
        var log = ImmutableList<string>.Empty;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var rolled = !string.Equals(row.Platform, Platform, StringComparison.Ordinal);
            var restartsBefore = Updater.Restarts;
            var swapsBefore = Loader.SwapsByModule;
            var downloadsBefore = Registry.Downloads;
            var loadedBefore = loaded;

            if (i == 0)
            {
                Volatile.Write(ref platform, row.Platform);
                await Install(row, ct, shapeRecord);
            }
            else
            {
                if (rolled)
                {
                    // A platform roll: a new process on the new platform boots the SAME module volume.
                    Volatile.Write(ref platform, row.Platform);
                    var skips = ImmutableList<string>.Empty;
                    Volatile.Write(ref loaded, Boot((m, r) => skips = skips.Add($"{m}: {r}")));
                    if (!skips.IsEmpty)
                        return LadderVerdict.Fail(row, $"the boot on {row.Platform} skipped {string.Join("; ", skips)}", log);
                }

                foreach (var (module, (version, floor)) in row.Published)
                    Registry.Serve(Package(module), version, floor);
                PublishFeed();
                await Reconciler.ReconcileNow().Timeout(TestTimeouts.Convergence).Await(ct);

                var activated = await ActivateNewRequests(seenRequests, ct);
                seenRequests = activated.Seen;
                log = log.AddRange(activated.Log);
            }

            var restarts = Updater.Restarts - restartsBefore;
            var problems = ImmutableList<string>.Empty;
            if (row.Roll != rolled)
                problems = problems.Add($"expected roll={row.Roll}, measured roll={rolled}");
            if (Updater.Patches > 0)
                problems = problems.Add($"the module lane patched the image {Updater.Patches} time(s) — a module step must never roll the platform");
            if (restarts != row.Restarts)
                problems = problems.Add($"expected {row.Restarts} restart(s) this step, measured {restarts}");

            foreach (var (module, expected) in row.Loaded)
            {
                var actual = loaded.TryGetValue(module, out var l) ? l.Version : "(nothing)";
                if (!string.Equals(actual, expected, StringComparison.Ordinal))
                    problems = problems.Add($"{module}: expected {expected} loaded, measured {actual}");
            }

            // Row 0 is the install itself; how it activated is the premise, not a ladder step.
            foreach (var (module, path) in i == 0 ? ImmutableDictionary<string, string>.Empty : row.Paths)
            {
                var swapped = Loader.SwapsByModule.GetValueOrDefault(module) - swapsBefore.GetValueOrDefault(module);
                var changed = !loadedBefore.TryGetValue(module, out var before)
                              || !loaded.TryGetValue(module, out var after)
                              || before.Directory != after.Directory;
                var problem = path switch
                {
                    LadderPath.Live when swapped != 1 => $"{module}: expected a live swap, measured {swapped} swap(s)",
                    LadderPath.Live when restarts != 0 => $"{module}: expected live, but the step restarted {restarts} time(s)",
                    LadderPath.Restart when swapped != 0 => $"{module}: expected a restart, but it was swapped live",
                    LadderPath.Restart when restarts != 1 => $"{module}: expected exactly one restart, measured {restarts}",
                    LadderPath.Restart when !changed => $"{module}: the restart did not change what it serves",
                    LadderPath.None when changed || swapped != 0 => $"{module}: expected no change, but it moved",
                    LadderPath.Declined => await Declined(module, row, downloadsBefore, changed, requireNamedDecline, ct),
                    _ => null,
                };
                if (problem is not null)
                    problems = problems.Add(problem);
            }

            var reading = $"[{row.Step}] on {Platform}: loaded "
                          + string.Join(", ", loaded.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value.Version}"))
                          + $"; restarts {Updater.Restarts}; swaps {string.Join(", ", Loader.SwapsByModule.Select(kv => $"{kv.Key}×{kv.Value}"))}";
            Output.WriteLine(reading);
            log = log.Add(reading);
            if (!problems.IsEmpty)
                return LadderVerdict.Fail(row, string.Join("; ", problems), log);
        }
        return LadderVerdict.Pass(log);
    }

    /// <summary>A declined module: nothing of the declined version was downloaded and it still serves
    /// what it served. With <paramref name="requireNamed"/>, the install record must also NAME the
    /// declined floor and the running platform — row A4's naming half, which fails today (see
    /// <see cref="ModuleUpdateLadderDeclineIsNamedTest"/>).</summary>
    private async Task<string?> Declined(
        string module, LadderRow row, ImmutableList<string> downloadsBefore, bool changed, bool requireNamed, CancellationToken ct)
    {
        var (version, floor) = row.Published[module];
        var package = Package(module);
        if (Registry.Downloads.Skip(downloadsBefore.Count).Contains($"{package}@{version}"))
            return $"{module}: the declined {version} was downloaded";
        if (changed)
            return $"{module}: declined, but what it serves changed";
        if (!requireNamed)
            return null;
        var record = await Access.RunAsSystem(() => Mesh.GetMeshNodeStream($"{PackageInstaller.InstalledPartition}/{package}").Take(1))
            .Select(n => n.ContentAs<PackageManifest>(Mesh.JsonSerializerOptions))
            .Timeout(TestTimeouts.Convergence).Await(ct);
        var held = record?.HeldUpdate ?? "";
        return held.Contains(floor, StringComparison.Ordinal) && held.Contains(Platform, StringComparison.Ordinal)
            ? null
            : $"{module}: declined, but no node names it — the install record's heldUpdate is '{held}', "
              + $"which does not name the floor {floor} and the running platform {Platform}";
    }

    /// <summary>Drives every request the reconcile filed to its end. A live request completes on its
    /// own. A restart request completes when the process that boots after its restart reports.</summary>
    private async Task<(ImmutableHashSet<string> Seen, ImmutableList<string> Log)> ActivateNewRequests(
        ImmutableHashSet<string> seen, CancellationToken ct)
    {
        var log = ImmutableList<string>.Empty;
        var listed = await Access.RunAsSystem(() => Mesh.ServiceProvider.GetRequiredService<IMeshService>()
                .Query<MeshNode>(MeshQueryRequest.FromQuery(
                    $"namespace:{ModuleReloadRequest.Namespace} scope:children nodeType:{ModuleReloadRequest.NodeType}"))
                .Take(1))
            .Select(change => change.Items.Select(n => n.Path).ToImmutableList())
            .Timeout(TestTimeouts.Convergence).Await(ct);
        foreach (var path in listed.Where(p => !seen.Contains(p)))
        {
            seen = seen.Add(path);
            var settled = await AwaitRequest(path,
                r => ModuleReloadStatus.IsTerminal(r.Status)
                     || (r.Status == ModuleReloadStatus.AwaitingRestart && r.RestartRequestedAt is not null && r.ActivationDetail is not null),
                ct);
            if (settled.Status == ModuleReloadStatus.AwaitingRestart)
            {
                // The pod the restart created boots the module volume on the running platform and
                // reports what IT loaded.
                Volatile.Write(ref loaded, Boot());
                var restarted = new ModuleReloadAgent
                {
                    StartedAt = settled.RestartRequestedAt!.Value.AddSeconds(30),
                    LoadedGenerations = () => loaded.ToImmutableDictionary(kv => kv.Key, kv => kv.Value.Directory),
                    LoadedNames = () => loaded.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase),
                };
                await restarted.Report(Mesh, path, settled, "restarted-pod-" + Updater.Restarts, swapFailure: null)
                    .DefaultIfEmpty().Timeout(TestTimeouts.Convergence).Await(ct);
                settled = await AwaitRequest(path, r => ModuleReloadStatus.IsTerminal(r.Status), ct);
            }
            log = log.Add($"request {path}: {settled.Status} via {settled.Activation} — {settled.Reason}"
                          + (settled.Failure is null ? "" : $" — {settled.Failure}"));
            if (settled.Status != ModuleReloadStatus.Done)
                Output.WriteLine($"request {path} ended {settled.Status}: {settled.Failure}");
        }
        return (seen, log);
    }

    private Task<ModuleReloadRequest> AwaitRequest(string path, Func<ModuleReloadRequest, bool> until, CancellationToken ct) =>
        Access.RunAsSystem(() => Mesh.GetMeshNodeStream(path))
            .Select(node => node.ContentAs<ModuleReloadRequest>(Mesh.JsonSerializerOptions))
            .Where(r => r is not null && until(r))
            .Select(r => r!)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);

    // ───────────────────────────────────────────────────────────── step 0: the install

    /// <summary>Installs every module at the row-0 publication: install record on the Auto policy, a
    /// SYNC-OWNED partition whose sync is at OLD content (row E1), and the bundle landed through the
    /// install path. The process then boots it.</summary>
    private async Task Install(LadderRow row, CancellationToken ct, Func<PackageManifest, PackageManifest>? shapeRecord)
    {
        PackagePlatformFloorGate.RunningVersion(Mesh).Should().Be(row.Platform, "the running-platform seam is what every floor decision reads");
        foreach (var module in Modules)
        {
            var (version, floor) = row.Published[module.Name];
            Registry.Serve(module.Package, version, floor);
            var manifest = new PackageManifest
            {
                Id = module.Package,
                Name = module.Package,
                Version = "1.0.0",
                TargetPartition = module.Package,
                Module = module.Name,
                ModuleVersion = "content-v1",
                UpdatePolicy = PackageUpdatePolicy.Auto,
                AutoUpdate = true,
            };
            var record = MeshNode.FromPath($"{PackageInstaller.InstalledPartition}/{module.Package}") with
            {
                NodeType = PackageInstaller.PackageNodeType,
                Name = module.Package,
                State = MeshNodeState.Active,
                Content = shapeRecord is null ? manifest : shapeRecord(manifest),
            };
            await Access.RunAsSystem(() => NodeFactory.CreateOrUpdateNode(record)).Timeout(TestTimeouts.Convergence).Await(ct);
            await SyncOwnedAtOldContent(module, ct);
            (await new PluginBundleClient(Mesh, RegistryUrl, Token).AdoptModule(module.Package, module.Name, record.Path)
                .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct)).Should().Be(1, $"the premise: {module.Name} {version} is landed");
        }
        PublishFeed();
        Volatile.Write(ref loaded, Boot());
    }

    private async Task SyncOwnedAtOldContent(LadderModule module, CancellationToken ct)
    {
        var config = new GitHubSyncConfig
        {
            RepositoryUrl = "https://github.com/Systemorph/Ladder",
            Branch = "main",
            ModuleVersions = ImmutableDictionary<string, string>.Empty.Add(module.Package, "content-v0"),
        };
        await Access.RunAsSystem(() => Mesh.ServiceProvider.GetRequiredService<IMeshService>().CreateNode(
                new MeshNode(GitHubSyncService.ConfigId, module.Package)
                {
                    Name = "GitHub Sync",
                    NodeType = GitHubSyncService.ConfigNodeType,
                    State = MeshNodeState.Active,
                    Content = config,
                }).Take(1))
            .Timeout(TestTimeouts.Convergence).Await(ct);
        var owner = await PartitionContentOwnership.Observe(Mesh, module.Package).Take(1).Timeout(TestTimeouts.Convergence).Await(ct);
        owner.Owner.Should().Be(PartitionContentOwner.SyncSource, "the premise of row E1: the partition is sync-owned");
    }

    /// <summary>The registry FEED: every package at the content identity installed here (the content
    /// lane has nothing to do), declaring the floor of the module bundle it currently serves.</summary>
    private void PublishFeed() =>
        Registry.Feed = Modules
            .Select(m => new PackageManifest
            {
                Id = m.Package,
                Name = m.Package,
                Version = "1.0.0",
                ModuleVersion = "content-v1",
                TargetPartition = m.Package,
                Module = m.Name,
                MinMeshVersion = Registry.Served(m.Package).Floor,
            })
            .ToImmutableList();

    private static string Package(string module) => Modules.Single(m => m.Name == module).Package;

    // ───────────────────────────────────────────────────────────── rows

    protected static LadderRow Row(
        string step, string platform, bool roll, int restarts,
        (LadderModule Module, string Version, string Floor)[] published,
        (LadderModule Module, string Version, string Path)[] expect) =>
        new(step, platform, roll, restarts,
            published.ToImmutableDictionary(p => p.Module.Name, p => (p.Version, p.Floor)),
            expect.ToImmutableDictionary(e => e.Module.Name, e => e.Version),
            expect.ToImmutableDictionary(e => e.Module.Name, e => e.Path));

    /// <summary>The ORDINARY instance: it follows its policy and rolls P3 one step after control.</summary>
    public static ImmutableList<LadderRow> OrdinaryTable =>
    [
        Row("0 P1: M_a 1.0, M_b 1.0 installed", P1, roll: false, restarts: 0,
            [(Ma, "1.0.0", P1), (Mb, "1.0.0", P1)],
            [(Ma, "1.0.0", LadderPath.None), (Mb, "1.0.0", LadderPath.None)]),
        Row("1 P1 → P2 roll, modules keep serving", P2, roll: true, restarts: 0,
            [(Ma, "1.0.0", P1), (Mb, "1.0.0", P1)],
            [(Ma, "1.0.0", LadderPath.None), (Mb, "1.0.0", LadderPath.None)]),
        Row("2 M_a 1.1 lands live", P2, roll: false, restarts: 0,
            [(Ma, "1.1.0", P2), (Mb, "1.0.0", P1)],
            [(Ma, "1.1.0", LadderPath.Live), (Mb, "1.0.0", LadderPath.None)]),
        Row("3 M_a 1.2 (built on an older P) lands live", P2, roll: false, restarts: 0,
            [(Ma, "1.2.0", P1), (Mb, "1.0.0", P1)],
            [(Ma, "1.2.0", LadderPath.Live), (Mb, "1.0.0", LadderPath.None)]),
        Row("4 M_b 1.1 lands, one restart", P2, roll: false, restarts: 1,
            [(Ma, "1.2.0", P1), (Mb, "1.1.0", P2)],
            [(Ma, "1.2.0", LadderPath.None), (Mb, "1.1.0", LadderPath.Restart)]),
        Row("5 M_a 1.3 (floor P3) declined on P2, M_b 1.2 lands", P2, roll: false, restarts: 1,
            [(Ma, "1.3.0", P3), (Mb, "1.2.0", P2)],
            [(Ma, "1.2.0", LadderPath.Declined), (Mb, "1.2.0", LadderPath.Restart)]),
        Row("6 P2 → P3 roll, M_a 1.3 lands live", P3, roll: true, restarts: 0,
            [(Ma, "1.3.0", P3), (Mb, "1.2.0", P2)],
            [(Ma, "1.3.0", LadderPath.Live), (Mb, "1.2.0", LadderPath.None)]),
    ];

    /// <summary>The CONTROL instance: always on the newest, so it rolls P3 the step it is promoted
    /// (step 5) and takes M_a 1.3 there — in the same wave as M_b 1.2, whose boot-time declaration
    /// makes the wave's one activation a restart.</summary>
    public static ImmutableList<LadderRow> ControlTable =>
    [
        .. OrdinaryTable.Take(5),
        Row("5 P3 promoted: control rolls, M_a 1.3 and M_b 1.2 land in one restart", P3, roll: true, restarts: 1,
            [(Ma, "1.3.0", P3), (Mb, "1.2.0", P2)],
            [(Ma, "1.3.0", LadderPath.Restart), (Mb, "1.2.0", LadderPath.Restart)]),
        Row("6 nothing new — control is already on the newest", P3, roll: false, restarts: 0,
            [(Ma, "1.3.0", P3), (Mb, "1.2.0", P2)],
            [(Ma, "1.3.0", LadderPath.None), (Mb, "1.2.0", LadderPath.None)]),
    ];

    // ───────────────────────────────────────────────────────────── seams

    private sealed class NoTags : IAcrTagLister
    {
        public Task<IReadOnlyList<string>> ListTagsAsync(string repository, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    /// <summary>The Kubernetes seam, recording every same-image restart and every image patch.</summary>
    protected sealed class RecordingUpdater : IDeploymentUpdater
    {
        private int restarts;
        private int patches;

        public int Restarts => Volatile.Read(ref restarts);
        public int Patches => Volatile.Read(ref patches);
        public bool CanPatch => true;

        public Task<DateTimeOffset?> LastRolledAtAsync(CancellationToken ct) =>
            Task.FromResult<DateTimeOffset?>(DateTimeOffset.UtcNow.AddMinutes(-1));

        public Task PatchToVersionAsync(string versionTag, CancellationToken ct)
        {
            Interlocked.Increment(ref patches);
            return Task.CompletedTask;
        }

        public Task<bool> RestartAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref restarts);
            return Task.FromResult(true);
        }
    }

    /// <summary>The reload's live call site, answered per module: a module that declares boot-time
    /// infrastructure cannot be swapped; any other swaps the landed generation into this process.</summary>
    protected sealed class LadderLoader(ModuleUpdateLadderScenario scenario) : IModuleLiveActivation
    {
        private ImmutableDictionary<string, int> swaps = ImmutableDictionary<string, int>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);

        public ImmutableDictionary<string, int> SwapsByModule => swaps;

        public bool CanSwap(string module) => !Modules.Single(m => m.Name == module).BootTime;

        public IObservable<ModuleSwapOutcome> Swap(string module, string reason) => Observable.Defer(() =>
        {
            ImmutableInterlocked.AddOrUpdate(ref swaps, module, 1, (_, n) => n + 1);
            scenario.SwapIn(module);
            return Observable.Return(new ModuleSwapOutcome(true));
        });
    }

    /// <summary>The plugin registry: a feed, and one module bundle per package at a settable version
    /// and floor. Every (package, version) carries different real assembly bytes, so each lands as
    /// its own generation.</summary>
    protected sealed class LadderRegistry : HttpMessageHandler, IHostHandler
    {
        private static readonly ImmutableArray<string> ByteSources =
        [
            typeof(BundleReader).Assembly.Location,
            typeof(ModuleAdoptOutcome).Assembly.Location,
            typeof(MeshNode).Assembly.Location,
            typeof(GitHubSyncConfig).Assembly.Location,
            typeof(ModuleReloadRequest).Assembly.Location,
        ];

        private ImmutableList<string> downloads = ImmutableList<string>.Empty;
        private ImmutableDictionary<string, (string Version, string? Floor)> served =
            ImmutableDictionary<string, (string, string?)>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<PackageManifest> Feed { get; set; } = [];

        /// <summary>Every download, as <c>package@version</c>.</summary>
        public ImmutableList<string> Downloads => downloads;

        public void Serve(string package, string version, string? floor) =>
            ImmutableInterlocked.Update(ref served, s => s.SetItem(package, (version, floor)));

        public (string Version, string? Floor) Served(string package) => served[package];

        public bool Serves(string host) => string.Equals(host, RegistryHost, StringComparison.OrdinalIgnoreCase);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/api/plugins")
                return Ok(PluginRegistryPayloads.List(Feed));
            if (request.Method == HttpMethod.Get && path == $"{PluginBundleClient.RoutePrefix}/index.json")
                return Ok(JsonSerializer.Serialize(new
                {
                    frameworkMvid = "s1234567890abcdef1234567890abcdef",
                    bundles = served.Select(kv => new
                    {
                        plugin = kv.Key,
                        version = kv.Value.Version,
                        url = $"{RegistryUrl}{PluginBundleClient.RoutePrefix}/{kv.Key}/{kv.Value.Version}",
                        module = Modules.Single(m => m.Package == kv.Key).Name,
                        minMeshVersion = kv.Value.Floor,
                        frameworkMvid = "s1234567890abcdef1234567890abcdef",
                    }).ToArray(),
                }, PluginRegistryPayloads.Json));
            if (request.Method == HttpMethod.Get && path.StartsWith($"{PluginBundleClient.RoutePrefix}/", StringComparison.Ordinal))
            {
                var segments = path[(PluginBundleClient.RoutePrefix.Length + 1)..].Split('/');
                if (segments.Length == 2 && served.TryGetValue(segments[0], out var bundle) && bundle.Version == segments[1])
                {
                    ImmutableInterlocked.Update(ref downloads, d => d.Add($"{segments[0]}@{bundle.Version}"));
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(Bundle(segments[0], bundle.Version, bundle.Floor)),
                    });
                }
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Ok(string body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });

        private static byte[] Bundle(string package, string version, string? floor)
        {
            var module = Modules.Single(m => m.Package == package).Name;
            // Landing is content-addressed: identical bytes would land as the SAME generation, and
            // "which generation is loaded" could not tell N from N+1.
            var minor = int.Parse(version.Split('.')[1], System.Globalization.CultureInfo.InvariantCulture);
            var bytesFrom = ByteSources[minor % ByteSources.Length];
            var manifestJson = JsonSerializer.Serialize(new
            {
                plugin = package,
                version,
                frameworkMvid = "s1234567890abcdef1234567890abcdef",
                module = new { assemblyName = module, assemblies = new[] { module + ".dll" }, minMeshVersion = floor },
            });
            var buffer = new MemoryStream();
            NuGetPackageWriter.Write(
                buffer,
                new PackagingManifest(package, "MeshWeaver.Plugin." + package, version, package, null, []),
                "3.0.0",
                [
                    new NuGetPackageWriter.Entry(
                        NuGetPackageWriter.ModuleEntryPathFor(module + ".dll"),
                        () => new MemoryStream(File.ReadAllBytes(bytesFrom))),
                ],
                manifestJson);
            return buffer.ToArray();
        }
    }
}

/// <summary>One module of the ladder: its package, its entry assembly, and whether it declares
/// boot-time infrastructure (activated by a restart, never swapped live).</summary>
public sealed record LadderModule(string Package, string Name, bool BootTime);

/// <summary>How a module activated in a step — open string constants.</summary>
public static class LadderPath
{
    public const string Live = "live";
    public const string Restart = "restart";
    public const string None = "none";
    public const string Declined = "declined";
}

/// <summary>One matrix row: the running platform and the published set, with what must hold after it.</summary>
public sealed record LadderRow(
    string Step,
    string Platform,
    bool Roll,
    int Restarts,
    ImmutableDictionary<string, (string Version, string Floor)> Published,
    ImmutableDictionary<string, string> Loaded,
    ImmutableDictionary<string, string> Paths);

/// <summary>Where a walk failed, and what it measured on the way.</summary>
public sealed record LadderVerdict(string? FailedStep, string? Problem, ImmutableList<string> Log)
{
    public static LadderVerdict Pass(ImmutableList<string> log) => new(null, null, log);

    public static LadderVerdict Fail(LadderRow row, string problem, ImmutableList<string> log) => new(row.Step, problem, log);

    public override string ToString() =>
        (FailedStep is null ? "every step held" : $"FAILED at [{FailedStep}]: {Problem}")
        + Environment.NewLine + string.Join(Environment.NewLine, Log);
}

/// <summary>The ordinary instance walks its table.</summary>
public class ModuleUpdateLadderMatrixTest(ITestOutputHelper output) : ModuleUpdateLadderScenario(output)
{
    [Fact(Timeout = 600_000)]
    public async Task Ordinary_WalksTheLadder_EveryStepHolds()
    {
        var verdict = await Walk(OrdinaryTable, TestContext.Current.CancellationToken);
        verdict.FailedStep.Should().BeNull(verdict.ToString());
    }
}

/// <summary>The control instance walks ITS table — on the newest platform first, so the module whose
/// floor is the newest platform lands there first.</summary>
public class ModuleUpdateLadderControlMatrixTest(ITestOutputHelper output) : ModuleUpdateLadderScenario(output)
{
    [Fact(Timeout = 600_000)]
    public async Task Control_WalksTheLadder_EveryStepHolds()
    {
        var verdict = await Walk(ControlTable, TestContext.Current.CancellationToken);
        verdict.FailedStep.Should().BeNull(verdict.ToString());
    }
}

/// <summary>
/// 🚨 The NEGATIVE CONTROL of the matrix. The ordinary table runs unchanged, but <c>M_a</c> is pinned
/// (<c>updatePolicy: None</c>). That is a real production hold, the one package-side hold the policy
/// keeps (row A6). The walk must fail at step 2, the first step where <c>M_a</c> should move, and at no
/// earlier step. A matrix that cannot fail there measures nothing.
/// </summary>
public class ModuleUpdateLadderMatrixNegativeControlTest(ITestOutputHelper output) : ModuleUpdateLadderScenario(output)
{
    [Fact(Timeout = 600_000)]
    public async Task APinnedPackage_FailsTheMatrixAtTheFirstModuleStep()
    {
        var verdict = await Walk(OrdinaryTable, TestContext.Current.CancellationToken,
            shapeRecord: m => m.Id == Ma.Package
                ? m with { UpdatePolicy = PackageUpdatePolicy.None, AutoUpdate = false, UpdatePolicySetAt = DateTimeOffset.UtcNow }
                : m);
        verdict.FailedStep.Should().Be(OrdinaryTable[2].Step, verdict.ToString());
        verdict.Problem.Should().Contain($"{Ma.Name}: expected 1.1.0 loaded, measured 1.0.0");
    }
}

/// <summary>
/// 🚨 <b>Row A4's naming half — FAILS TODAY.</b> A module publication whose declared floor is above
/// the running platform is declined on the unattended auto-update lane: nothing is downloaded, and
/// the running version keeps serving. Both halves are pinned by the matrix. But the decline is named
/// only in an Information log line (<c>PluginBundleClient.AdoptModuleOutcome</c> logs the
/// <c>ModuleUpdateDecision</c> verdict and returns). No node carries it: the install record's
/// <c>heldUpdate</c> stays empty, and no reload request is filed. The record's hold sentence is
/// written only by the CONTENT lane (<c>PackageUpdateReconciler</c>), which has nothing to do when
/// the package's content identity did not move. So an operator reading the instance cannot see why
/// the module is not updating. Measured on this branch: the walk fails with "declined, but no node
/// names it — the install record's heldUpdate is ''". An explicit reload request DOES name it
/// (<c>ModuleReloadByRestartTest.ANewerVersionAboveTheFloor_IsDeclinedByName_AndTheRunningVersionKeepsServing</c>).
/// </summary>
public class ModuleUpdateLadderDeclineIsNamedTest(ITestOutputHelper output) : ModuleUpdateLadderScenario(output)
{
    [Fact(Timeout = 600_000, Skip = "FAILS TODAY — a module declined for its floor on the auto-update lane is named on no node "
                                    + "(row A4, Doc/Architecture/ModuleUpdateLadder → 'Rows that fail today'). Un-skip with the fix.")]
    public async Task AModuleDeclinedForItsFloor_IsNamedOnTheInstallRecord()
    {
        ImmutableList<LadderRow> rows =
        [
            OrdinaryTable[0],
            OrdinaryTable[1],
            Row("2 M_a 1.1 (floor P3) published on P2 — declined, and NAMED", P2, roll: false, restarts: 0,
                [(Ma, "1.1.0", P3), (Mb, "1.0.0", P1)],
                [(Ma, "1.0.0", LadderPath.Declined), (Mb, "1.0.0", LadderPath.None)]),
        ];
        var verdict = await Walk(rows, TestContext.Current.CancellationToken, requireNamedDecline: true);
        verdict.FailedStep.Should().BeNull(verdict.ToString());
    }
}
