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
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.SelfUpdate;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using PackagingManifest = MeshWeaver.Plugin.Packaging.PluginManifest;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The pure rules of a module reload (<c>Doc/Architecture/ModuleReload</c>): what a request may say,
/// which replica reports count, and how a process states what it has loaded. Each rule with its
/// negative control beside it.
/// </summary>
public class ModuleReloadRulesTest
{
    private static ModuleReloadRequest Request(params ModuleReloadItem[] items) => new()
    {
        Module = "M",
        Reason = "test",
        Items = [.. items],
    };

    private static ModuleReloadItem Item(string target) => new() { Module = "M", TargetVersion = target };

    private static ModuleReloadReplica Replica(string process, DateTimeOffset started, string loaded, string? swapFailure = null) => new()
    {
        Process = process,
        StartedAt = started,
        ReportedAt = started.AddSeconds(5),
        Loaded = ImmutableDictionary<string, string>.Empty.Add("M", loaded),
        SwapFailure = swapFailure,
    };

    [Fact]
    public void AReload_NeedsAReason_AndAModuleNameThatIsAName()
    {
        ModuleReload.Validate(new ModuleReloadRequest { Module = "MeshWeaver.AI", Reason = "x" }).Should().BeNull();
        ModuleReload.Validate(new ModuleReloadRequest { Module = null, Reason = "x" }).Should().BeNull("blank means every module");
        ModuleReload.Validate(new ModuleReloadRequest { Module = "MeshWeaver.AI", Reason = " " }).Should().Contain("reason");
        ModuleReload.Validate(new ModuleReloadRequest { Module = "Plugins/AI", Reason = "x" }).Should().Contain("not a module");
    }

    [Fact]
    public void AfterARestart_OnlyAProcessBootedAfterIt_Counts()
    {
        var restart = new DateTimeOffset(2026, 10, 5, 6, 0, 0, TimeSpan.Zero);
        var request = Request(Item("1.21.0")) with { RestartRequestedAt = restart };

        // The old pod, reporting the old version on its way out — not evidence of anything.
        var onlyOld = request with
        {
            Replicas = ImmutableDictionary<string, ModuleReloadReplica>.Empty
                .Add("old", Replica("old", restart.AddHours(-3), "1.20.4")),
        };
        ModuleReload.Evaluate(onlyOld, _ => true).Should().BeNull("nothing booted after the restart has reported yet");

        var booted = onlyOld with { Replicas = onlyOld.Replicas.Add("new", Replica("new", restart.AddMinutes(2), "1.21.0")) };
        var loaded = ModuleReload.Evaluate(booted, _ => true)!;
        loaded.Loaded.Should().BeTrue();
        loaded.Detail.Should().Contain("M 1.21.0");

        // Negative control: a NEW process that still loads the old version is a named failure.
        var stale = onlyOld with { Replicas = onlyOld.Replicas.Add("new", Replica("new", restart.AddMinutes(2), "1.20.4")) };
        var red = ModuleReload.Evaluate(stale, _ => true)!;
        red.Loaded.Should().BeFalse();
        red.Detail.Should().Contain("new loads M 1.20.4, not 1.21.0");
    }

    [Fact]
    public void AReportFromAGoneProcess_DoesNotCount()
    {
        var restart = DateTimeOffset.UtcNow.AddMinutes(-10);
        var request = Request(Item("2.0.0")) with
        {
            RestartRequestedAt = restart,
            Replicas = ImmutableDictionary<string, ModuleReloadReplica>.Empty
                .Add("gone", Replica("gone", restart.AddMinutes(1), "1.0.0")),
        };
        ModuleReload.Evaluate(request, p => p != "gone").Should().BeNull();
        ModuleReload.Evaluate(request, _ => true)!.Loaded.Should().BeFalse("the control: counted, it is a mismatch");
    }

    [Fact]
    public void AFailedLiveSwap_IsTheFallbackSignal()
    {
        var swap = DateTimeOffset.UtcNow.AddMinutes(-1);
        var request = Request(Item("2.0.0")) with
        {
            LiveSwapRequestedAt = swap,
            Replicas = ImmutableDictionary<string, ModuleReloadReplica>.Empty
                .Add("p", Replica("p", swap.AddHours(-1), "1.0.0", swapFailure: "M: the new context would not load") with { ReportedAt = swap.AddSeconds(3) }),
        };
        var verdict = ModuleReload.Evaluate(request, _ => true)!;
        verdict.SwapFailed.Should().BeTrue();
        verdict.Detail.Should().Contain("the new context would not load");
    }

    [Fact]
    public void LoadedVersions_ReadTheGenerationThisProcessLoaded_AgainstTheRecord()
    {
        var activation = new ModuleActivationList
        {
            Entries =
            [
                new ModuleActivationEntry { Name = "A", Directory = "A@new", Version = "2.0.0", PreviousDirectory = "A@old", PreviousVersion = "1.0.0" },
                new ModuleActivationEntry { Name = "B", Directory = "B@new", Version = "3.0.0" },
            ],
        };
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A", "B", "C" };

        var head = ModuleReloadAgent.LoadedVersions(["A"], activation,
            new Dictionary<string, string> { ["A"] = "A@new" }, names);
        head["A"].Should().Be("2.0.0");
        var previous = ModuleReloadAgent.LoadedVersions(["A"], activation,
            new Dictionary<string, string> { ["A"] = "A@old" }, names);
        previous["A"].Should().Be("1.0.0", "the previous generation is still a version, named as itself");
        var image = ModuleReloadAgent.LoadedVersions(["B", "C", "D"], activation, new Dictionary<string, string>(), names);
        image["B"].Should().Contain("image's own copy");
        image["D"].Should().Be("not loaded");
    }
}

/// <summary>
/// 🚨 <b>"Reload M on this instance", end to end through the real lanes</b> — the registry's bundle
/// index and download route (a fake HTTP registry), the real <see cref="RegistryUpdateReconciler"/>,
/// <see cref="PluginBundleClient"/> and <see cref="ModuleLandingService"/> on a temp module root,
/// the real request node, executor and agent, and the real <see cref="SelfUpdateHostedService"/> as
/// the restart path (only its Kubernetes updater is recorded instead of called).
///
/// <para>What is simulated, and only that: which generation a PROCESS has loaded (a test process
/// cannot load the landed bytes as module <c>M</c>), and the process that boots after the restart.
/// Everything the reload decides and writes is real.</para>
/// </summary>
public abstract class ModuleReloadScenario(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected const string RegistryHost = "registry.module-reload.test";
    protected const string RegistryUrl = "https://" + RegistryHost;
    protected const string Token = "mwi_module_reload_test";
    protected const string Package = "ReloadPkg";
    protected const string Module = "MeshWeaver.ReloadModule";

    private readonly string landingRoot = Path.Combine(Path.GetTempPath(), "mw-reload-" + Guid.NewGuid().ToString("N"));

    protected ReloadRegistry Registry { get; } = new();

    protected RecordingUpdater Updater { get; } = new();

    /// <summary>What the test PROCESS has loaded: module → generation leaf.</summary>
    private ImmutableDictionary<string, string> loaded = ImmutableDictionary<string, string>.Empty;

    protected void SetLoaded(string module, string generation) =>
        ImmutableInterlocked.Update(ref loaded, l => l.SetItem(module, generation));

    protected virtual IModuleLiveActivation? LiveActivation => null;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        Directory.CreateDirectory(landingRoot);
        return base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .ConfigureServices(services =>
            {
                services
                    .AddSingleton<IHttpClientFactory>(new HostRoutingClientFactory(Registry))
                    .AddSingleton(FloorFixture.Pin)
                    .AddSingleton(new ModuleLandingService(baseDirectory: landingRoot))
                    .AddSingleton(new PluginCatalogOptions
                    {
                        Registries = [new PluginRegistryReference { Name = "test", Url = RegistryUrl, Token = Token }],
                    })
                    .AddSingleton(new ModuleReloadAgent
                    {
                        LoadedGenerations = () => loaded,
                        LoadedNames = () => loaded.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase),
                    })
                    .AddSingleton<IModuleActivationRestart>(sp => new SelfUpdateHostedService(
                        sp.GetRequiredService<IMessageHub>(), new NoTags(), Updater, new SelfUpdateOptions()));
                if (LiveActivation is { } live)
                    services.AddSingleton(live);
                return services;
            });
    }

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

    protected ModuleActivationEntry Head() =>
        ModuleActivationSidecar.Read(landingRoot).Entries.Single(e => e.Name == Module);

    /// <summary>The instance runs M@1.1.0: installed, landed, and loaded by this process.</summary>
    protected async Task RunningVersionOne(CancellationToken ct)
    {
        FloorFixture.AssertTheFloorHolds(Mesh);
        var manifest = new PackageManifest
        {
            Id = Package,
            Name = Package,
            Version = "1.1.0",
            TargetPartition = Package,
            Module = Module,
            // Notify: the UNATTENDED lane would decline an update — the reload is attended and must not.
            AutoUpdate = false,
        };
        var record = MeshNode.FromPath($"{PackageInstaller.InstalledPartition}/{Package}") with
        {
            NodeType = PackageInstaller.PackageNodeType,
            Name = Package,
            State = MeshNodeState.Active,
            Content = manifest,
        };
        await Access.RunAsSystem(() => NodeFactory.CreateOrUpdateNode(record)).Timeout(TestTimeouts.Convergence).Await(ct);

        Registry.Serve("1.1.0", floor: null);
        (await new PluginBundleClient(Mesh, RegistryUrl, Token).AdoptModule(Package, Module, record.Path)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct)).Should().Be(1, "the premise: 1.1.0 is landed");
        var head = Head();
        head.Version.Should().Be("1.1.0");
        SetLoaded(Module, head.Directory!);
    }

    protected async Task<string> Reload(CancellationToken ct)
    {
        var ticket = await ModuleReload.Request(Mesh, new ModuleReloadRequest
            {
                Module = Module,
                Reason = "AI 1.21 is needed by Hosting (the 2026-10-05 incident shape)",
                RequestedBy = "test",
            })
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        ticket.Refusal.Should().BeNull();
        return ticket.Path!;
    }

    protected Task<ModuleReloadRequest> AwaitRequest(string path, Func<ModuleReloadRequest, bool> until, CancellationToken ct) =>
        Access.RunAsSystem(() => Mesh.GetMeshNodeStream(path))
            .Select(node => node.ContentAs<ModuleReloadRequest>(Mesh.JsonSerializerOptions))
            .Where(r => r is not null && until(r))
            .Select(r => r!)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);

    private sealed class NoTags : IAcrTagLister
    {
        public Task<IReadOnlyList<string>> ListTagsAsync(string repository, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    /// <summary>The Kubernetes seam, recording every same-image restart.</summary>
    protected sealed class RecordingUpdater : IDeploymentUpdater
    {
        private int restarts;

        public int Restarts => Volatile.Read(ref restarts);
        public bool CanPatch => true;

        public Task<DateTimeOffset?> LastRolledAtAsync(CancellationToken ct) =>
            // Rolled a minute ago — INSIDE the poller's one-hour floor: an explicit reload must not
            // be deferred by it (it asks once, stamped on its node).
            Task.FromResult<DateTimeOffset?>(DateTimeOffset.UtcNow.AddMinutes(-1));

        public Task PatchToVersionAsync(string versionTag, CancellationToken ct) => Task.CompletedTask;

        public Task<bool> RestartAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref restarts);
            return Task.FromResult(true);
        }
    }

    /// <summary>The plugin registry: an empty feed, and ONE module bundle at a settable version and
    /// floor — each version carrying different real assembly bytes, so each lands as its own
    /// generation.</summary>
    protected sealed class ReloadRegistry : HttpMessageHandler, IHostHandler
    {
        private ImmutableList<string> downloads = ImmutableList<string>.Empty;
        private (string Version, string? Floor) served = ("1.0.0", null);

        public ImmutableList<string> Downloads => downloads;

        public void Serve(string version, string? floor) => served = (version, floor);

        public bool Serves(string host) => string.Equals(host, RegistryHost, StringComparison.OrdinalIgnoreCase);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var (version, floor) = served;
            if (request.Method == HttpMethod.Get && path == "/api/plugins")
                return Ok(PluginRegistryPayloads.List([]));
            if (request.Method == HttpMethod.Get && path == $"{PluginBundleClient.RoutePrefix}/index.json")
                return Ok(JsonSerializer.Serialize(new
                {
                    frameworkMvid = "s1234567890abcdef1234567890abcdef",
                    bundles = new[]
                    {
                        new
                        {
                            plugin = Package,
                            version,
                            url = $"{RegistryUrl}{PluginBundleClient.RoutePrefix}/{Package}/{version}",
                            module = Module,
                            minMeshVersion = floor,
                            frameworkMvid = "s1234567890abcdef1234567890abcdef",
                        },
                    },
                }, PluginRegistryPayloads.Json));
            if (request.Method == HttpMethod.Get && path.StartsWith($"{PluginBundleClient.RoutePrefix}/", StringComparison.Ordinal))
            {
                ImmutableInterlocked.Update(ref downloads, d => d.Add(version));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bundle(version, floor)) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Ok(string body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });

        private static byte[] Bundle(string version, string? floor)
        {
            // Different real assemblies per version: landing is content-addressed, so identical
            // bytes would land as the SAME generation and "which generation is loaded" could not
            // tell N from N+1.
            var bytesFrom = version == "1.1.0"
                ? typeof(BundleReader).Assembly.Location
                : typeof(ModuleAdoptOutcome).Assembly.Location;
            var manifestJson = JsonSerializer.Serialize(new
            {
                plugin = Package,
                version,
                frameworkMvid = "s1234567890abcdef1234567890abcdef",
                module = new { assemblyName = Module, assemblies = new[] { Module + ".dll" }, minMeshVersion = floor },
            });
            var buffer = new MemoryStream();
            NuGetPackageWriter.Write(
                buffer,
                new PackagingManifest(Package, "MeshWeaver.Plugin." + Package, version, Package, null, []),
                "3.0.0",
                [
                    new NuGetPackageWriter.Entry(
                        NuGetPackageWriter.ModuleEntryPathFor(Module + ".dll"),
                        () => new MemoryStream(File.ReadAllBytes(bytesFrom))),
                ],
                manifestJson);
            return buffer.ToArray();
        }
    }
}

/// <summary>The restart lane: a process that cannot swap live — N+1 activates by exactly ONE restart.</summary>
public class ModuleReloadByRestartTest(ITestOutputHelper output) : ModuleReloadScenario(output)
{
    /// <summary>
    /// 🚨 THE ACCEPTANCE SCENARIO. M@1.1.0 is running, 1.2.0 is published with a floor this platform
    /// meets, and a reload is requested: 1.2.0 is resolved and landed, exactly one restart is
    /// requested through the self-update restart path (inside the poller's roll floor, which a
    /// reload does not wait on), the request waits for a process booted after it — and once that
    /// process reports 1.2.0 loaded, the request is Done with the per-replica version on the node.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task ARunningVersion_ReloadsToTheNewestCompatible_ByExactlyOneRestart()
    {
        var ct = TestContext.Current.CancellationToken;
        await RunningVersionOne(ct);
        Registry.Serve("1.2.0", floor: FloorFixture.Below);

        var path = await Reload(ct);
        var waiting = await AwaitRequest(path, r => r.Status == ModuleReloadStatus.AwaitingRestart && r.ActivationDetail?.StartsWith(ModuleRestartKinds.Restarted) == true, ct);

        var item = waiting.Items.Single();
        item.FoundVersion.Should().Be("1.2.0");
        item.FoundFloor.Should().Be(FloorFixture.Below);
        item.TargetVersion.Should().Be("1.2.0");
        item.RunningVersion.Should().Be("1.1.0");
        item.Landed.Should().BeTrue();
        item.Failure.Should().BeNull();
        waiting.Activation.Should().Be(ModuleReloadActivation.Restart);
        waiting.RestartRequestedAt.Should().NotBeNull();
        Updater.Restarts.Should().Be(1, "exactly one restart, and the roll floor does not defer an explicit reload");
        Head().Version.Should().Be("1.2.0", "the activation record names N+1 for the boot that follows");
        Registry.Downloads.Should().Equal("1.1.0", "1.2.0");

        // ── The pod the restart created boots and reports what IT loaded. ────────────────────
        var restarted = new ModuleReloadAgent
        {
            StartedAt = waiting.RestartRequestedAt!.Value.AddSeconds(30),
            LoadedGenerations = () => ImmutableDictionary<string, string>.Empty.Add(Module, Head().Directory!),
        };
        await restarted.Report(Mesh, path, waiting, "restarted-pod", swapFailure: null)
            .DefaultIfEmpty().Timeout(TestTimeouts.Convergence).Await(ct);

        var done = await AwaitRequest(path, r => ModuleReloadStatus.IsTerminal(r.Status), ct);
        done.Status.Should().Be(ModuleReloadStatus.Done, done.Failure ?? "");
        done.Replicas["restarted-pod"].Loaded[Module].Should().Be("1.2.0");
        Updater.Restarts.Should().Be(1, "the report completes the request; it never asks for a second restart");
    }

    /// <summary>
    /// The negative control of the acceptance scenario: the pod that booted after the restart still
    /// loads N (its previous generation) — the request is RED, naming the replica and both versions.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task AReplicaThatStillLoadsTheOldVersionAfterTheRestart_TurnsTheRequestRed()
    {
        var ct = TestContext.Current.CancellationToken;
        await RunningVersionOne(ct);
        var oldGeneration = Head().Directory!;
        Registry.Serve("1.2.0", floor: FloorFixture.Below);

        var path = await Reload(ct);
        var waiting = await AwaitRequest(path, r => r.Status == ModuleReloadStatus.AwaitingRestart && r.RestartRequestedAt is not null && r.ActivationDetail is not null, ct);
        Head().PreviousDirectory.Should().Be(oldGeneration, "the premise: N is kept as N+1's fallback");

        var restarted = new ModuleReloadAgent
        {
            StartedAt = waiting.RestartRequestedAt!.Value.AddSeconds(30),
            LoadedGenerations = () => ImmutableDictionary<string, string>.Empty.Add(Module, oldGeneration),
        };
        await restarted.Report(Mesh, path, waiting, "restarted-pod", swapFailure: null)
            .DefaultIfEmpty().Timeout(TestTimeouts.Convergence).Await(ct);

        var red = await AwaitRequest(path, r => ModuleReloadStatus.IsTerminal(r.Status), ct);
        red.Status.Should().Be(ModuleReloadStatus.Failed);
        red.Failure.Should().Contain("restarted-pod loads MeshWeaver.ReloadModule 1.1.0, not 1.2.0");
        Updater.Restarts.Should().Be(1);
    }

    /// <summary>
    /// 🚨 A floor ABOVE the running platform: declined by name, nothing downloaded, no restart, and
    /// N keeps serving (the activation record still names it).
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task ANewerVersionAboveTheFloor_IsDeclinedByName_AndTheRunningVersionKeepsServing()
    {
        var ct = TestContext.Current.CancellationToken;
        await RunningVersionOne(ct);
        Registry.Serve("1.2.0", floor: FloorFixture.Above);

        var path = await Reload(ct);
        var red = await AwaitRequest(path, r => ModuleReloadStatus.IsTerminal(r.Status), ct);

        red.Status.Should().Be(ModuleReloadStatus.Failed);
        var item = red.Items.Single();
        item.FoundVersion.Should().Be("1.2.0");
        item.FoundFloor.Should().Be(FloorFixture.Above);
        item.Failure.Should().StartWith("declined").And.Contain(FloorFixture.Above).And.Contain(FloorFixture.Running);
        red.Failure.Should().Contain(FloorFixture.Above);
        red.Activation.Should().Be(ModuleReloadActivation.NotNeeded);
        Updater.Restarts.Should().Be(0, "nothing landed, so nothing is restarted");
        Registry.Downloads.Should().Equal(["1.1.0"], "the held bundle is not even downloaded");
        Head().Version.Should().Be("1.1.0", "N keeps serving");
    }

    /// <summary>The control for "nothing to do": the newest compatible version is the one running —
    /// Done at once, NotNeeded, no download, no restart.</summary>
    [Fact(Timeout = 240_000)]
    public async Task AReloadOfTheVersionAlreadyRunning_IsDoneWithoutARestart()
    {
        var ct = TestContext.Current.CancellationToken;
        await RunningVersionOne(ct);

        var path = await Reload(ct);
        var done = await AwaitRequest(path, r => ModuleReloadStatus.IsTerminal(r.Status), ct);

        done.Status.Should().Be(ModuleReloadStatus.Done, done.Failure ?? "");
        done.Activation.Should().Be(ModuleReloadActivation.NotNeeded);
        done.Items.Single().TargetVersion.Should().Be("1.1.0");
        Updater.Restarts.Should().Be(0);
        Registry.Downloads.Should().Equal(["1.1.0"]);
    }

    /// <summary>A module nobody installed is refused by name, with nothing asked of the registry.</summary>
    [Fact(Timeout = 240_000)]
    public async Task AModuleThatIsNotInstalled_IsRefusedByName()
    {
        var ct = TestContext.Current.CancellationToken;
        var ticket = await ModuleReload.Request(Mesh, new ModuleReloadRequest { Module = "MeshWeaver.NotHere", Reason = "test" })
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        var red = await AwaitRequest(ticket.Path!, r => ModuleReloadStatus.IsTerminal(r.Status), ct);
        red.Status.Should().Be(ModuleReloadStatus.Failed);
        red.Failure.Should().Contain("'MeshWeaver.NotHere' is not an installed module here");
        Registry.Downloads.Should().BeEmpty();
    }
}

/// <summary>The live lane: a process whose loader can swap the module — no restart at all; and a
/// swap that fails falls back to exactly one restart.</summary>
public class ModuleReloadLiveTest(ITestOutputHelper output) : ModuleReloadScenario(output)
{
    private readonly FakeLoader loader = new();

    protected override IModuleLiveActivation? LiveActivation => loader;

    [Fact(Timeout = 240_000)]
    public async Task ASwappableModule_GoesLive_WithoutARestart()
    {
        var ct = TestContext.Current.CancellationToken;
        await RunningVersionOne(ct);
        Registry.Serve("1.2.0", floor: FloorFixture.Below);
        loader.OnSwap = () => SetLoaded(Module, Head().Directory!);

        var path = await Reload(ct);
        var done = await AwaitRequest(path, r => ModuleReloadStatus.IsTerminal(r.Status), ct);

        done.Status.Should().Be(ModuleReloadStatus.Done, done.Failure ?? "");
        done.Activation.Should().Be(ModuleReloadActivation.Live);
        done.Replicas.Values.Should().ContainSingle().Which.Loaded[Module].Should().Be("1.2.0");
        loader.Swaps.Should().Be(1);
        Updater.Restarts.Should().Be(0, "a live swap needs no restart");
    }

    [Fact(Timeout = 240_000)]
    public async Task AFailedLiveSwap_FallsBackToExactlyOneRestart()
    {
        var ct = TestContext.Current.CancellationToken;
        await RunningVersionOne(ct);
        Registry.Serve("1.2.0", floor: FloorFixture.Below);
        loader.Failure = "the new context would not load";

        var path = await Reload(ct);
        var waiting = await AwaitRequest(path, r => r.Status == ModuleReloadStatus.AwaitingRestart && r.RestartRequestedAt is not null && r.ActivationDetail?.StartsWith(ModuleRestartKinds.Restarted) == true, ct);

        waiting.Activation.Should().Be(ModuleReloadActivation.Restart);
        waiting.Log.Should().Contain(line => line.Contains("the new context would not load"));
        Updater.Restarts.Should().Be(1);
    }

    private sealed class FakeLoader : IModuleLiveActivation
    {
        private int swaps;

        public int Swaps => Volatile.Read(ref swaps);
        public Action? OnSwap { get; set; }
        public string? Failure { get; set; }

        public bool CanSwap(string module) => true;

        public IObservable<ModuleSwapOutcome> Swap(string module, string reason) => Observable.Defer(() =>
        {
            Interlocked.Increment(ref swaps);
            if (Failure is { } failure)
                return Observable.Return(new ModuleSwapOutcome(false, failure));
            OnSwap?.Invoke();
            return Observable.Return(new ModuleSwapOutcome(true));
        });
    }
}
