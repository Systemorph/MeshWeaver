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
    public void Loaded_WaitsForEveryRunningProcess_ButAFailureIsDecisiveAtOnce()
    {
        var swap = DateTimeOffset.UtcNow.AddMinutes(-1);
        string[] roster = ["a", "b"];
        var first = Request(Item("2.0.0")) with
        {
            LiveSwapRequestedAt = swap,
            Replicas = ImmutableDictionary<string, ModuleReloadReplica>.Empty
                .Add("a", Replica("a", swap.AddHours(-1), "2.0.0") with { ReportedAt = swap.AddSeconds(3) }),
        };
        ModuleReload.Evaluate(first, _ => true, roster)
            .Should().BeNull("b is running and has not reported — its swap may yet fail and need the restart");
        ModuleReload.Evaluate(first, _ => true)!.Loaded
            .Should().BeTrue("the control: without a roster the one counted report is all there is");

        var both = first with
        {
            Replicas = first.Replicas.Add("b", Replica("b", swap.AddHours(-1), "2.0.0") with { ReportedAt = swap.AddSeconds(5) }),
        };
        ModuleReload.Evaluate(both, _ => true, roster)!.Loaded.Should().BeTrue();

        var bFailed = first with
        {
            Replicas = first.Replicas.Add("b", Replica("b", swap.AddHours(-1), "1.0.0", swapFailure: "M: would not load") with { ReportedAt = swap.AddSeconds(5) }),
        };
        ModuleReload.Evaluate(bFailed, _ => true, roster)!.SwapFailed.Should().BeTrue();

        var restart = swap;
        var oldStillUp = Request(Item("2.0.0")) with
        {
            RestartRequestedAt = restart,
            Replicas = ImmutableDictionary<string, ModuleReloadReplica>.Empty
                .Add("new", Replica("new", restart.AddMinutes(1), "2.0.0"))
                .Add("old", Replica("old", restart.AddHours(-2), "1.0.0")),
        };
        ModuleReload.Evaluate(oldStillUp, _ => true, ["new", "old"])
            .Should().BeNull("an old pod still running after the restart serves the old version");
        ModuleReload.Evaluate(oldStillUp, _ => true, ["new"])!.Loaded.Should().BeTrue("once it is gone, the restart is complete");
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

    /// <summary>
    /// 🚨 A CRASH is Faulted, a decided answer is Failed — and only Faulted is ever retried. The
    /// rule is over the items: one crashed module makes the whole request Faulted, because its
    /// answer is still unknown.
    /// </summary>
    [Fact]
    public void ACrashedItem_IsFaulted_ADecidedFailure_IsFailed()
    {
        var crashed = new ModuleReloadItem { Module = "M", Failure = "landing failed: 503", Transient = true };
        var declined = new ModuleReloadItem { Module = "N", Failure = "declined — floor above the platform" };
        var fine = new ModuleReloadItem { Module = "O", TargetVersion = "1.0.0" };

        ModuleReload.OutcomeOf([fine], failure: null).Should().Be(ModuleReloadStatus.Done);
        ModuleReload.OutcomeOf([declined, fine], "N: declined").Should().Be(ModuleReloadStatus.Failed);
        ModuleReload.OutcomeOf([crashed], "M: landing failed").Should().Be(ModuleReloadStatus.Faulted);
        ModuleReload.OutcomeOf([declined, crashed], "both").Should().Be(ModuleReloadStatus.Faulted,
            "the crashed module's answer is unknown, so the request is retried");
        ModuleReloadStatus.IsTerminal(ModuleReloadStatus.Faulted).Should().BeFalse("a crash is never final");
        ModuleReloadStatus.IsTerminal(ModuleReloadStatus.Failed).Should().BeTrue();
    }

    /// <summary>The retry is due on the pass cadence, doubled per attempt and capped in the doubling
    /// only; nothing but Faulted is ever due.</summary>
    [Fact]
    public void AFaultedRequest_IsDue_OnThePassCadence_DoubledPerAttempt()
    {
        var at = new DateTimeOffset(2026, 10, 5, 20, 0, 0, TimeSpan.Zero);
        var unit = TimeSpan.FromMinutes(30);
        var faulted = new ModuleReloadRequest { Reason = "r", Status = ModuleReloadStatus.Faulted, FaultedAt = at };

        ModuleReload.RetryDueAt(faulted, unit).Should().Be(at + unit);
        ModuleReload.RetryDueAt(faulted with { Attempt = 1 }, unit).Should().Be(at + 2 * unit);
        ModuleReload.RetryDueAt(faulted with { Attempt = 3 }, unit).Should().Be(at + 8 * unit);
        ModuleReload.RetryDueAt(faulted with { Attempt = 50 }, unit)
            .Should().Be(at + (1 << ModuleReload.MaxBackoffDoublings) * unit, "the doubling stops; the retries do not");

        // Negative controls: a decided failure, a finished or a running request is never due.
        ModuleReload.RetryDueAt(faulted with { Status = ModuleReloadStatus.Failed }, unit).Should().BeNull();
        ModuleReload.RetryDueAt(faulted with { Status = ModuleReloadStatus.Done }, unit).Should().BeNull();
        ModuleReload.RetryDueAt(faulted with { Status = ModuleReloadStatus.Landing }, unit).Should().BeNull();
    }

    /// <summary>The re-arm runs the request again from the top: next attempt, the faulted attempt's
    /// executor-owned fields cleared, the audit log kept.</summary>
    [Fact]
    public void Rearm_StartsTheNextAttempt_FromRequested_KeepingTheLog()
    {
        var at = DateTimeOffset.UtcNow;
        var faulted = new ModuleReloadRequest
        {
            Module = "M",
            Reason = "r",
            Status = ModuleReloadStatus.Faulted,
            Attempt = 2,
            FaultedAt = at,
            Failure = "landing failed: 503",
            Items = [new ModuleReloadItem { Module = "M", Failure = "landing failed: 503", Transient = true }],
            RestartRequestedAt = at,
            LiveSwapRequestedAt = at,
            Activation = ModuleReloadActivation.Restart,
            Replicas = ImmutableDictionary<string, ModuleReloadReplica>.Empty.Add("p", Replica("p", at, "1.0.0")),
            Log = ["first line"],
        };

        var next = ModuleReload.Rearm(faulted, at.AddMinutes(30));

        next.Status.Should().Be(ModuleReloadStatus.Requested);
        next.Attempt.Should().Be(3);
        next.Items.Should().BeEmpty();
        next.Failure.Should().BeNull();
        next.FaultedAt.Should().BeNull();
        next.RestartRequestedAt.Should().BeNull();
        next.LiveSwapRequestedAt.Should().BeNull();
        next.Activation.Should().BeNull();
        next.Replicas.Should().BeEmpty();
        next.Module.Should().Be("M");
        next.Reason.Should().Be("r");
        next.Log.Should().HaveCount(2).And.Contain("first line");
        next.Log[^1].Should().Contain("retry 3").And.Contain("landing failed: 503");
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

    /// <summary>A second installed package, served only when <see cref="ReloadRegistry.ServesSecond"/>
    /// is set — for a reload that covers more than one module.</summary>
    protected const string SecondPackage = "ReloadPkgTwo";
    protected const string SecondModule = "MeshWeaver.ReloadModuleTwo";

    private readonly string landingRoot = Path.Combine(Path.GetTempPath(), "mw-reload-" + Guid.NewGuid().ToString("N"));

    protected ReloadRegistry Registry { get; } = new();

    protected RecordingUpdater Updater { get; } = new();

    /// <summary>What the test PROCESS has loaded: module → generation leaf.</summary>
    private ImmutableDictionary<string, string> loaded = ImmutableDictionary<string, string>.Empty;

    protected void SetLoaded(string module, string generation) =>
        ImmutableInterlocked.Update(ref loaded, l => l.SetItem(module, generation));

    protected virtual IModuleLiveActivation? LiveActivation => null;

    /// <summary>The restart lane, when a test needs one that answers on its own terms; null takes the
    /// real self-updater over <see cref="RecordingUpdater"/>.</summary>
    protected virtual IModuleActivationRestart? RestartLane => null;

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
                    .AddSingleton<IModuleActivationRestart>(sp => RestartLane ?? new SelfUpdateHostedService(
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

    protected ModuleActivationEntry Head() => HeadOf(Module);

    protected ModuleActivationEntry HeadOf(string module) =>
        ModuleActivationSidecar.Read(landingRoot).Entries.Single(e => e.Name == module);

    /// <summary>The instance runs M@1.1.0: installed, landed, and loaded by this process.</summary>
    protected async Task RunningVersionOne(CancellationToken ct, Func<PackageManifest, PackageManifest>? shape = null)
    {
        FloorFixture.AssertTheFloorHolds(Mesh);
        Registry.Serve("1.1.0", floor: null);
        await InstallRunning(Package, Module, ct, shape);
    }

    /// <summary>Installs <paramref name="package"/> declaring <paramref name="module"/>, lands the
    /// version the registry serves for it (1.1.0), and records this process as loading it.</summary>
    protected async Task InstallRunning(string package, string module, CancellationToken ct,
        Func<PackageManifest, PackageManifest>? shape = null)
    {
        var manifest = new PackageManifest
        {
            Id = package,
            Name = package,
            Version = "1.1.0",
            TargetPartition = package,
            Module = module,
            // Notify: the UNATTENDED lane would decline an update — the reload is attended and must not.
            AutoUpdate = false,
        };
        var record = MeshNode.FromPath($"{PackageInstaller.InstalledPartition}/{package}") with
        {
            NodeType = PackageInstaller.PackageNodeType,
            Name = package,
            State = MeshNodeState.Active,
            Content = shape is null ? manifest : shape(manifest),
        };
        await Access.RunAsSystem(() => NodeFactory.CreateOrUpdateNode(record)).Timeout(TestTimeouts.Convergence).Await(ct);

        (await new PluginBundleClient(Mesh, RegistryUrl, Token).AdoptModule(package, module, record.Path)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct)).Should().Be(1, $"the premise: {package} 1.1.0 is landed");
        var head = HeadOf(module);
        head.Version.Should().Be("1.1.0");
        SetLoaded(module, head.Directory ?? throw new InvalidOperationException($"{module} landed without a directory"));
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

        /// <summary>The portal Deployment's rollout strategy an instance reboot reads — non-disruptive by default.</summary>
        public RolloutStrategyReading? Strategy { get; set; } = new("RollingUpdate", "1", "0", 2);

        public Task<RolloutStrategyReading?> ReadRolloutStrategyAsync(CancellationToken ct) => Task.FromResult(Strategy);
    }

    /// <summary>The plugin registry: an empty feed, and ONE module bundle at a settable version and
    /// floor — each version carrying different real assembly bytes, so each lands as its own
    /// generation.</summary>
    protected sealed class ReloadRegistry : HttpMessageHandler, IHostHandler
    {
        private ImmutableList<string> downloads = ImmutableList<string>.Empty;
        private (string Version, string? Floor) served = ("1.0.0", null);

        /// <summary>What the registry FEED lists (`GET /api/plugins`) — empty unless a test publishes.</summary>
        public IReadOnlyList<PackageManifest> Feed { get; set; } = [];

        public ImmutableList<string> Downloads => downloads;

        public void Serve(string version, string? floor) => served = (version, floor);

        /// <summary>While true, the bundle index answers 503 — a registry that is briefly down, the
        /// TRANSIENT fault a reload must survive.</summary>
        public bool IndexDown { get; set; }

        /// <summary>When set, the index also lists <see cref="SecondPackage"/> at the served version.</summary>
        public bool ServesSecond { get; set; }

        /// <summary>The package whose bundle download crashes (throws), or null for none.</summary>
        public string? CrashDownloadOf { get; set; }

        /// <summary>When set, the bundle DOWNLOAD answers this status instead of the bytes — the index
        /// still advertises the version, so the reload reaches the download and meets the answer.</summary>
        public HttpStatusCode? DownloadStatus { get; set; }

        /// <summary>When true, the bundle download times out (the transfer never reaches an answer).</summary>
        public bool DownloadTimesOut { get; set; }

        public bool Serves(string host) => string.Equals(host, RegistryHost, StringComparison.OrdinalIgnoreCase);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var (version, floor) = served;
            if (request.Method == HttpMethod.Get && path == "/api/plugins")
                return Ok(PluginRegistryPayloads.List(Feed));
            if (request.Method == HttpMethod.Get && path == $"{PluginBundleClient.RoutePrefix}/index.json" && IndexDown)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("registry briefly down", Encoding.UTF8, "text/plain"),
                });
            if (request.Method == HttpMethod.Get && path == $"{PluginBundleClient.RoutePrefix}/index.json")
            {
                var listed = ServesSecond
                    ? new[] { (Package, Module), (SecondPackage, SecondModule) }
                    : new[] { (Package, Module) };
                return Ok(JsonSerializer.Serialize(new
                {
                    frameworkMvid = "s1234567890abcdef1234567890abcdef",
                    bundles = listed.Select(b => new
                    {
                        plugin = b.Item1,
                        version,
                        url = $"{RegistryUrl}{PluginBundleClient.RoutePrefix}/{b.Item1}/{version}",
                        module = b.Item2,
                        minMeshVersion = floor,
                        frameworkMvid = "s1234567890abcdef1234567890abcdef",
                    }).ToArray(),
                }, PluginRegistryPayloads.Json));
            }
            if (request.Method == HttpMethod.Get && path.StartsWith($"{PluginBundleClient.RoutePrefix}/", StringComparison.Ordinal))
            {
                var plugin = Uri.UnescapeDataString(path[(PluginBundleClient.RoutePrefix.Length + 1)..].Split('/')[0]);
                // A transfer that CRASHES (the connection dropped), not a refusal: an exception, so
                // the adopt reports it as transient — what a reload must treat as Faulted.
                if (string.Equals(plugin, CrashDownloadOf, StringComparison.Ordinal))
                    throw new HttpRequestException($"the connection to the registry was reset while downloading {plugin}");
                if (DownloadTimesOut)
                    throw new TimeoutException($"the download of {plugin} timed out");
                if (DownloadStatus is { } status)
                    return Task.FromResult(new HttpResponseMessage(status)
                    {
                        Content = new StringContent($"answered {(int)status}", Encoding.UTF8, "text/plain"),
                    });
                var module = plugin == SecondPackage ? SecondModule : Module;
                ImmutableInterlocked.Update(ref downloads, d => d.Add(version));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bundle(plugin, module, version, floor)) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Ok(string body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });

        private static byte[] Bundle(string package, string module, string version, string? floor)
        {
            // Different real assemblies per version: landing is content-addressed, so identical
            // bytes would land as the SAME generation and "which generation is loaded" could not
            // tell N from N+1.
            var bytesFrom = version == "1.1.0"
                ? typeof(BundleReader).Assembly.Location
                : typeof(ModuleAdoptOutcome).Assembly.Location;
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

        public IObservable<ModuleReloadSwapOutcome> Swap(string module, string reason) => Observable.Defer(() =>
        {
            Interlocked.Increment(ref swaps);
            if (Failure is { } failure)
                return Observable.Return(new ModuleReloadSwapOutcome(false, failure));
            OnSwap?.Invoke();
            return Observable.Return(new ModuleReloadSwapOutcome(true));
        });
    }
}

/// <summary>
/// 🚨 <b>A crashed reload step is never final</b> (Plugins#2893 review): a registry that is briefly
/// down makes the request <see cref="ModuleReloadStatus.Faulted"/>, the reconcile pass's retry
/// (<see cref="ModuleReload.RetryFaulted"/>) re-arms it, and the next attempt finishes — while a
/// DECIDED failure stays <see cref="ModuleReloadStatus.Failed"/> and is never retried.
/// </summary>
public class ModuleReloadFaultedTest(ITestOutputHelper output) : ModuleReloadScenario(output)
{
    [Fact(Timeout = 240_000)]
    public async Task ATransientFault_IsFaulted_TheNextPassRetriesIt_AndItSucceeds()
    {
        var ct = TestContext.Current.CancellationToken;
        await RunningVersionOne(ct);
        Registry.IndexDown = true;

        var path = await Reload(ct);
        var faulted = await AwaitRequest(path, r => r.Status is ModuleReloadStatus.Faulted || ModuleReloadStatus.IsTerminal(r.Status), ct);

        faulted.Status.Should().Be(ModuleReloadStatus.Faulted, faulted.Failure ?? "");
        faulted.Failure.Should().Contain("503");
        faulted.Attempt.Should().Be(0);
        faulted.FaultedAt.Should().NotBeNull();
        var faultedAt = faulted.FaultedAt ?? throw new InvalidOperationException("a Faulted request carries FaultedAt");
        faulted.CompletedAt.Should().BeNull("a fault is not an end");
        faulted.Items.Single().Transient.Should().BeTrue();

        // The pass before the backoff is due re-arms nothing — the backoff is honoured.
        var unit = TimeSpan.FromMinutes(30);
        (await ModuleReload.RetryFaulted(Mesh, unit, faultedAt.AddMinutes(1))
                .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct))
            .Should().BeEmpty("the first retry is due one pass interval after the fault");

        // The registry is back, and the next pass is due: the request is re-armed and finishes.
        Registry.IndexDown = false;
        (await ModuleReload.RetryFaulted(Mesh, unit, faultedAt + unit)
                .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct))
            .Should().Equal([path]);

        var done = await AwaitRequest(path, r => ModuleReloadStatus.IsTerminal(r.Status), ct);
        done.Status.Should().Be(ModuleReloadStatus.Done, done.Failure ?? "");
        done.Attempt.Should().Be(1);
        done.Items.Single().TargetVersion.Should().Be("1.1.0");
        done.Log.Should().Contain(line => line.Contains("retry 1"));
        Updater.Restarts.Should().Be(0);
    }

    [Fact(Timeout = 240_000)]
    public async Task ADecidedFailure_IsFailed_AndIsNeverRetried()
    {
        var ct = TestContext.Current.CancellationToken;
        await RunningVersionOne(ct);
        Registry.Serve("1.2.0", floor: FloorFixture.Above);

        var path = await Reload(ct);
        var red = await AwaitRequest(path, r => r.Status is ModuleReloadStatus.Faulted || ModuleReloadStatus.IsTerminal(r.Status), ct);
        red.Status.Should().Be(ModuleReloadStatus.Failed, "a floor above the platform is a decided answer");
        red.Items.Single().Transient.Should().BeFalse();

        (await ModuleReload.RetryFaulted(Mesh, TimeSpan.Zero, DateTimeOffset.UtcNow.AddDays(1))
                .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct))
            .Should().BeEmpty("a decided failure is final — retrying it would get the same answer");
        var after = await AwaitRequest(path, _ => true, ct);
        after.Status.Should().Be(ModuleReloadStatus.Failed);
        after.Attempt.Should().Be(0);
    }
}

/// <summary>
/// 🚨 <b>A crash beside an activation is still Faulted</b> (MeshWeaver#6172 review). One module's
/// adopt CRASHES while another lands a new version and goes live. The request's end state is
/// decided in <c>Evaluate</c> from the items the activation write persisted, so the crashed item's
/// <see cref="ModuleReloadItem.Transient"/> flag must survive that write. If it did not, the end
/// state would read <see cref="ModuleReloadStatus.Failed"/>, and a crash would be final.
/// </summary>
public class ModuleReloadFaultedBesideActivationTest(ITestOutputHelper output) : ModuleReloadScenario(output)
{
    private readonly SwapLoader loader = new();

    protected override IModuleLiveActivation? LiveActivation => loader;

    [Fact(Timeout = 240_000)]
    public async Task OneModulesCrashedAdopt_BesideAnotherModulesLiveActivation_IsFaulted()
    {
        var ct = TestContext.Current.CancellationToken;
        FloorFixture.AssertTheFloorHolds(Mesh);
        Registry.ServesSecond = true;
        Registry.Serve("1.1.0", floor: null);
        await InstallRunning(Package, Module, ct);
        await InstallRunning(SecondPackage, SecondModule, ct);

        // N+1 for both: the first lands and swaps live, the second's download crashes.
        Registry.Serve("1.2.0", floor: FloorFixture.Below);
        Registry.CrashDownloadOf = SecondPackage;
        loader.OnSwap = module => SetLoaded(module, HeadOf(module).Directory
            ?? throw new InvalidOperationException($"{module} has no landed directory"));

        var ticket = await ModuleReload.Request(Mesh, new ModuleReloadRequest
            {
                Reason = "one module crashes beside another's activation (#6172 review)",
                RequestedBy = "test",
            })
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        ticket.Refusal.Should().BeNull();
        var path = ticket.Path ?? throw new InvalidOperationException("an accepted request carries its path");

        var end = await AwaitRequest(path, r => r.Status is ModuleReloadStatus.Faulted || ModuleReloadStatus.IsTerminal(r.Status), ct);

        end.Activation.Should().Be(ModuleReloadActivation.Live, "the premise: the other module activated live");
        loader.Swapped.Should().Equal([Module], "only the module that landed is swapped");
        var crashed = end.Items.Single(i => i.Module == SecondModule);
        crashed.Failure.Should().Contain("reset");
        crashed.Transient.Should().BeTrue("the activation write must persist the crashed item's flag");
        end.Items.Single(i => i.Module == Module).Failure.Should().BeNull();
        end.Status.Should().Be(ModuleReloadStatus.Faulted,
            "a crash beside a successful activation is still a crash — never final: " + (end.Failure ?? ""));
        end.FaultedAt.Should().NotBeNull();
        end.CompletedAt.Should().BeNull("a fault is not an end");
    }

    private sealed class SwapLoader : IModuleLiveActivation
    {
        private ImmutableList<string> swapped = ImmutableList<string>.Empty;

        public ImmutableList<string> Swapped => swapped;
        public Action<string>? OnSwap { get; set; }

        public bool CanSwap(string module) => true;

        public IObservable<ModuleReloadSwapOutcome> Swap(string module, string reason) => Observable.Defer(() =>
        {
            ImmutableInterlocked.Update(ref swapped, s => s.Add(module));
            OnSwap?.Invoke(module);
            return Observable.Return(new ModuleReloadSwapOutcome(true));
        });
    }
}

/// <summary>
/// 🚨 <b>A transient DOWNLOAD answer is Faulted, never Failed</b> (MeshWeaver#6172). The index
/// advertises N+1 and the bundle download itself answers: a 503, 429, 502, 504 or a timeout may
/// clear on its own, so the reload records <see cref="ModuleReloadStatus.Faulted"/> and the next
/// reconcile pass retries it; a 404 or 403 is the registry's decided answer, so the reload is
/// <see cref="ModuleReloadStatus.Failed"/> and never retried. The classification lives in ONE place
/// (<see cref="TransientRegistryFailure"/>).
/// </summary>
public class ModuleReloadTransientDownloadTest(ITestOutputHelper output) : ModuleReloadScenario(output)
{
    private static readonly TimeSpan PassInterval = TimeSpan.FromMinutes(30);

    /// <summary>The instance runs 1.1.0, the index advertises 1.2.0, and the DOWNLOAD meets
    /// <paramref name="download"/>'s answer. Returns the request's path and its first settled state.</summary>
    private async Task<(string Path, ModuleReloadRequest Settled)> ReloadMeeting(Action<ReloadRegistry> download, CancellationToken ct)
    {
        await RunningVersionOne(ct);
        Registry.Serve("1.2.0", floor: FloorFixture.Below);
        download(Registry);
        var path = await Reload(ct);
        var settled = await AwaitRequest(path, r => r.Status is ModuleReloadStatus.Faulted
                                                    || r.Status == ModuleReloadStatus.AwaitingRestart
                                                    || ModuleReloadStatus.IsTerminal(r.Status), ct);
        return (path, settled);
    }

    [Fact(Timeout = 240_000)]
    public async Task ADownloadAnswering503_IsFaulted_TheNextPassRetriesIt_AndItLands()
    {
        var ct = TestContext.Current.CancellationToken;
        var (path, faulted) = await ReloadMeeting(r => r.DownloadStatus = HttpStatusCode.ServiceUnavailable, ct);

        faulted.Status.Should().Be(ModuleReloadStatus.Faulted, "a 503 may clear on its own: " + (faulted.Failure ?? ""));
        faulted.Failure.Should().Contain("503");
        faulted.Items.Single().Transient.Should().BeTrue();
        faulted.CompletedAt.Should().BeNull("a fault is not an end");
        var faultedAt = faulted.FaultedAt ?? throw new InvalidOperationException("a Faulted request carries FaultedAt");

        // The registry recovers; the next due pass re-arms the request and it lands N+1.
        Registry.DownloadStatus = null;
        (await ModuleReload.RetryFaulted(Mesh, PassInterval, faultedAt + PassInterval)
                .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct))
            .Should().Equal([path]);

        var landed = await AwaitRequest(path,
            r => r.Attempt == 1 && (r.Status == ModuleReloadStatus.AwaitingRestart || ModuleReloadStatus.IsTerminal(r.Status)), ct);
        landed.Status.Should().Be(ModuleReloadStatus.AwaitingRestart, landed.Failure ?? "");
        landed.Items.Single().TargetVersion.Should().Be("1.2.0");
        landed.Items.Single().Failure.Should().BeNull();
        Registry.Downloads.Should().Contain("1.2.0", "the retry fetched the bundle the 503 withheld");
    }

    [Theory(Timeout = 240_000)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task ADownloadAnsweringAStatusThatMayClear_IsFaulted(HttpStatusCode status)
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, end) = await ReloadMeeting(r => r.DownloadStatus = status, ct);

        end.Status.Should().Be(ModuleReloadStatus.Faulted, $"{(int)status} may clear on its own: {end.Failure}");
        end.Items.Single().Transient.Should().BeTrue();
    }

    [Fact(Timeout = 240_000)]
    public async Task ADownloadThatTimesOut_IsFaulted()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, end) = await ReloadMeeting(r => r.DownloadTimesOut = true, ct);

        end.Status.Should().Be(ModuleReloadStatus.Faulted, "a timeout never reached an answer: " + (end.Failure ?? ""));
        end.Items.Single().Transient.Should().BeTrue();
    }

    [Theory(Timeout = 240_000)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ADownloadAnsweringADecidedStatus_IsFailed_AndNeverRetried(HttpStatusCode status)
    {
        var ct = TestContext.Current.CancellationToken;
        var (path, end) = await ReloadMeeting(r => r.DownloadStatus = status, ct);

        end.Status.Should().Be(ModuleReloadStatus.Failed, $"{(int)status} is the registry's decided answer: {end.Failure}");
        end.Items.Single().Transient.Should().BeFalse();
        (await ModuleReload.RetryFaulted(Mesh, TimeSpan.Zero, DateTimeOffset.UtcNow.AddDays(1))
                .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct))
            .Should().BeEmpty("a decided failure is final — retrying it would get the same answer");
        var after = await AwaitRequest(path, _ => true, ct);
        after.Status.Should().Be(ModuleReloadStatus.Failed);
        after.Attempt.Should().Be(0);
    }
}

/// <summary>
/// 🚨 <b>A failed restart ATTEMPT is Faulted; "this install cannot restart" is Failed</b>
/// (MeshWeaver#6172). The restart lane answers <see cref="ModuleRestartKinds.Faulted"/> when the
/// path exists but this attempt failed (a refused or unreachable hand-over, a call that threw) —
/// retried — and <see cref="ModuleRestartKinds.Unavailable"/> when the install has no way to
/// restart at all, which a retry would answer identically.
/// </summary>
public class ModuleReloadRestartAttemptTest(ITestOutputHelper output) : ModuleReloadScenario(output)
{
    private readonly ScriptedRestart lane = new();

    protected override IModuleActivationRestart? RestartLane => lane;

    [Fact(Timeout = 240_000)]
    public async Task AFailedRestartAttempt_IsFaulted_AndTheRetryAsksAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        await RunningVersionOne(ct);
        Registry.Serve("1.2.0", floor: FloorFixture.Below);
        lane.Answers = [ModuleRestartKinds.Faulted, ModuleRestartKinds.Restarted];

        var path = await Reload(ct);
        var faulted = await AwaitRequest(path, r => r.Status is ModuleReloadStatus.Faulted || ModuleReloadStatus.IsTerminal(r.Status), ct);
        faulted.Status.Should().Be(ModuleReloadStatus.Faulted, "a failed hand-over may get through next time: " + (faulted.Failure ?? ""));
        faulted.Failure.Should().Contain(ModuleRestartKinds.Faulted);
        var faultedAt = faulted.FaultedAt ?? throw new InvalidOperationException("a Faulted request carries FaultedAt");

        (await ModuleReload.RetryFaulted(Mesh, TimeSpan.FromMinutes(30), faultedAt + TimeSpan.FromMinutes(30))
                .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct))
            .Should().Equal([path]);
        // AwaitingRestart is written BEFORE the lane is asked; the lane's answer lands on ActivationDetail.
        var waiting = await AwaitRequest(path, r => r.Attempt == 1 && r.Status == ModuleReloadStatus.AwaitingRestart
                                                    && r.ActivationDetail?.StartsWith(ModuleRestartKinds.Restarted) == true, ct);
        waiting.Failure.Should().BeNull();
        lane.Asked.Should().Be(2, "the retry asks for the restart again — the failed attempt delivered nothing");
    }

    [Fact(Timeout = 240_000)]
    public async Task AnInstallThatCannotRestart_IsFailed_AndNeverRetried()
    {
        var ct = TestContext.Current.CancellationToken;
        await RunningVersionOne(ct);
        Registry.Serve("1.2.0", floor: FloorFixture.Below);
        lane.Answers = [ModuleRestartKinds.Unavailable];

        var path = await Reload(ct);
        var end = await AwaitRequest(path, r => r.Status is ModuleReloadStatus.Faulted || ModuleReloadStatus.IsTerminal(r.Status), ct);
        end.Status.Should().Be(ModuleReloadStatus.Failed, "no restart path is a decided answer: " + (end.Failure ?? ""));
        (await ModuleReload.RetryFaulted(Mesh, TimeSpan.Zero, DateTimeOffset.UtcNow.AddDays(1))
                .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct))
            .Should().BeEmpty();
        lane.Asked.Should().Be(1);
    }

    /// <summary>A restart lane that answers a scripted kind per call (the last one repeats).</summary>
    private sealed class ScriptedRestart : IModuleActivationRestart
    {
        private int asked;

        public ImmutableList<string> Answers { get; set; } = [ModuleRestartKinds.Restarted];

        public int Asked => Volatile.Read(ref asked);

        public IObservable<ModuleRestartOutcome> RequestRestart(string reason) => Observable.Defer(() =>
        {
            var n = Interlocked.Increment(ref asked) - 1;
            var kind = Answers[Math.Min(n, Answers.Count - 1)];
            return Observable.Return(new ModuleRestartOutcome(kind, $"scripted answer {n + 1}: {kind}"));
        });
    }
}

/// <summary>
/// The ONE classifier (<see cref="TransientRegistryFailure"/>): only the registry clients'
/// <see cref="RegistryRefusedException"/> is a decided refusal; a bare
/// <see cref="InvalidOperationException"/> from incidental code is a crash, retried (#6172 review).
/// </summary>
public class TransientRegistryFailureTest
{
    [Fact]
    public void ARegistryRefusal_IsDecided_AForeignInvalidOperation_IsACrash()
    {
        TransientRegistryFailure.IsTransient(new RegistryRefusedException("no manifest")).Should().BeFalse();
        TransientRegistryFailure.IsTransient(new InvalidOperationException("Sequence contains no elements")).Should().BeTrue();
        TransientRegistryFailure.IsTransient(new RegistryResponseException(HttpStatusCode.NotFound, "404")).Should().BeFalse();
        TransientRegistryFailure.IsTransient(new RegistryResponseException(HttpStatusCode.ServiceUnavailable, "503")).Should().BeTrue();
        TransientRegistryFailure.IsTransient(new AggregateException(new RegistryRefusedException("x"), new TimeoutException())).Should().BeFalse();
        TransientRegistryFailure.IsTransient(new AggregateException(new TimeoutException(), new InvalidOperationException("y"))).Should().BeTrue();
    }

    [Fact]
    public void TheFeedRead_AsksTheSameClassifier()
    {
        RegistryUpdateReconciler.ShouldRetryFeedRead(new RegistryRefusedException("no repository")).Should().BeFalse();
        RegistryUpdateReconciler.ShouldRetryFeedRead(new InvalidOperationException("incidental")).Should().BeTrue();
        RegistryUpdateReconciler.ShouldRetryFeedRead(new RegistryResponseException(HttpStatusCode.TooManyRequests, "429")).Should().BeTrue();
        RegistryUpdateReconciler.ShouldRetryFeedRead(new RegistryResponseException(HttpStatusCode.Forbidden, "403")).Should().BeFalse();
    }

    /// <summary>
    /// The HTTP bundle route's fault outcome asks the same classifier — the seam a reload reads
    /// <c>Transient</c> from. A refusal must come out decided (never retried); a 503/timeout/crash
    /// transient. Fails if the route marks every fault transient by itself again.
    /// </summary>
    [Fact]
    public void TheHttpBundleRoute_AsksTheSameClassifier()
    {
        PluginBundleClient.LandingFaultOutcome("https://r", new RegistryRefusedException("no bundle layer")).Transient.Should().BeFalse();
        PluginBundleClient.LandingFaultOutcome("https://r", new RegistryResponseException(HttpStatusCode.NotFound, "404")).Transient.Should().BeFalse();
        PluginBundleClient.LandingFaultOutcome("https://r", new RegistryResponseException(HttpStatusCode.ServiceUnavailable, "503")).Transient.Should().BeTrue();
        PluginBundleClient.LandingFaultOutcome("https://r", new TimeoutException()).Transient.Should().BeTrue();
        PluginBundleClient.LandingFaultOutcome("https://r", new InvalidOperationException("incidental")).Transient.Should().BeTrue();
    }
}
