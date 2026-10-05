using System;
using System.Collections.Immutable;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.SelfUpdate;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.SelfUpdate;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>"Running, update coming for a module, schedule restart" — live first</b> (policy
/// <c>module-live-update-default</c>, <c>Doc/Architecture/LiveModuleUpdate</c>).
///
/// <para>An instance RUNS module M at generation N (landed, activation-recorded, loaded in its own
/// context at boot). Generation N+1 ARRIVES through the real install path
/// (<see cref="ModuleLandingService.LandModule"/> — bytes on the volume, the activation entry moved,
/// the pending-restart marker raised). Then the instance's own self-update check runs — the path
/// that until this change ALWAYS scheduled a restart:</para>
/// <list type="bullet">
/// <item>a live-updatable M goes live in the process and NO restart is scheduled;</item>
/// <item>an M whose contributions cannot be re-applied, or whose live swap FAILS at runtime, keeps N
/// serving and schedules exactly ONE automatic restart, with the reason recorded by name;</item>
/// <item>two updates before the restart still schedule one restart, and the record names the
/// newest generation — the one that restart loads;</item>
/// <item>an update that arrives while the restart is in flight schedules no second one.</item>
/// </list>
/// <para>And the negative control: with the fallback suppressed (no check runs), the module that
/// did not go live stays landed-not-loaded, and the guard names it.</para>
///
/// <para>Only the documented IO seams are faked (the ACR listing and the k8s updater). The mesh,
/// the landing service, the module registry, the live updater and the self-update decision path are
/// real.</para>
/// </summary>
public sealed class ModuleUpdatesGoLiveTest : MonolithMeshTestBase
{
    private const string Module = "MeshWeaver.Test.GoesLive";
    private const string ProbePath = "GoesLiveProbe";
    private static TimeSpan Budget => TestTimeouts.Convergence;

    private static string Installed => ShippedReleaseSeed.InstalledPlatformVersion;
    private static string[] NothingNewer => ["1.9.0-ci.0", "2.0.0-ci.0", Installed.Split('+')[0]];

    private readonly string root = Path.Combine(Path.GetTempPath(), "mw-golive-" + Guid.NewGuid().ToString("N"));
    private string? bootEntry;

    public ModuleUpdatesGoLiveTest(ITestOutputHelper output) : base(output)
    {
    }

    /// <summary>The instance RUNNING M@N: generation g1 landed on the volume with its activation
    /// entry, and loaded at boot from that landed path — before the base builds the mesh.</summary>
    private string BootEntry
    {
        get
        {
            if (bootEntry is not null)
                return bootEntry;
            var directory = Path.Combine(root, "modules", $"{Module}@g1");
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, Module + ".dll"), Emit(ModuleSource(1)));
            ModuleActivationSidecar.Write(root, new ModuleActivationList
            {
                Entries = [new ModuleActivationEntry { Name = Module, Directory = $"{Module}@g1", Version = "1" }],
            });
            return bootEntry = Path.Combine(directory, Module + ".dll");
        }
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            .AddUpdatePolicyType()
            .AddGitHubSyncTypes()
            .AddPluginCatalog()
            .InstallAssemblies(BootEntry)
            .ConfigureServices(services =>
            {
                // The landing service and the activation reader rooted at THIS instance's volume.
                services.RemoveAll<ModuleLandingService>();
                services.AddSingleton(_ => new ModuleLandingService(baseDirectory: root));
                services.RemoveAll<PendingModuleActivations>();
                services.AddSingleton(sp => new PendingModuleActivations(root)
                {
                    IoPool = sp.GetRequiredService<IoPoolRegistry>().Get(IoPoolNames.FileSystem),
                    ModuleContexts = sp.GetRequiredService<ModuleContexts>(),
                });
                return services;
            });

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();
    private ModuleLandingService Landing => Mesh.ServiceProvider.GetRequiredService<ModuleLandingService>();
    private ModuleContexts Contexts => Mesh.ServiceProvider.GetRequiredService<ModuleContexts>();

    // ═════════════════════════════════════════ live: no restart

    [Fact(Timeout = 300_000)]
    public async Task ALiveUpdatableModuleUpdate_GoesLiveInTheProcess_AndSchedulesNoRestart()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(ct);
        (await NameAt(ProbePath, ct)).Should().Be("v1", "the arrangement: the instance runs M@N");

        await Land(ModuleSource(2), "2", ct);
        var updater = new RecordingUpdater();
        var verdict = await RunOneCheck(updater, ct);

        updater.Restarts.Should().Be(0, "a live-updatable module's update must schedule NO restart");
        verdict.Should().Contain("went LIVE");
        (await NameAt(ProbePath, ct)).Should().Be("v2", "M@N+1 serves in the running process");
        (Contexts.Current(Module)?.Location).Should().Contain($"{Module}@", "the landed generation is the current one");
        AssertNothingStuck();
    }

    // ═════════════════════════════════════════ the fallback: exactly one automatic restart

    [Fact(Timeout = 300_000)]
    public async Task ARestartRequiredUpdate_SchedulesExactlyOneAutomaticRestart_AndNKeepsServing()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(ct);
        (await NameAt(ProbePath, ct)).Should().Be("v1");

        await Land(ModuleSource(2, configuresMeshHub: true), "2", ct);
        var updater = new RecordingUpdater();
        var verdict = await RunOneCheck(updater, ct);

        updater.Restarts.Should().Be(1, "a module that cannot be swapped in-process is activated by ONE automatic restart");
        verdict.Should().Contain("RESTARTED");
        (await NameAt(ProbePath, ct)).Should().Be("v1", "N keeps serving until the restart — never a half-swapped state");
    }

    [Fact(Timeout = 300_000)]
    public async Task AnInjectedLiveFailure_FallsBackToExactlyOneRestart_WithTheReasonRecorded()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(ct);
        (await NameAt(ProbePath, ct)).Should().Be("v1");

        await Land(ModuleSource(2, throwingNodes: true), "2", ct);
        var live = await Mesh.ServiceProvider.GetRequiredService<ModuleLiveActivation>()
            .ActivatePending("test: inspect the failure").Timeout(Budget).Await(ct);
        live.NeedsRestart.Should().BeTrue();
        live.Describe().Should().Contain(Module).And.Contain("cannot be built",
            "the reason the live swap failed is recorded by name");

        var updater = new RecordingUpdater();
        await RunOneCheck(updater, ct);

        updater.Restarts.Should().Be(1, "a live swap that fails at runtime falls back to ONE automatic restart");
        (await NameAt(ProbePath, ct)).Should().Be("v1", "N keeps serving until the restart");
    }

    [Fact(Timeout = 300_000)]
    public async Task TwoUpdatesBeforeTheRestart_ScheduleOneRestart_AndTheRecordNamesTheNewest()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(ct);
        await Land(ModuleSource(2, configuresMeshHub: true), "2", ct);
        await Land(ModuleSource(3, configuresMeshHub: true), "3", ct);

        var updater = new RecordingUpdater();
        await RunOneCheck(updater, ct);

        updater.Restarts.Should().Be(1, "two pending generations of one module are ONE restart");
        var entry = (await Landing.GetActivation().Timeout(Budget).Await(ct)).Entries.Single(e => e.Name == Module);
        entry.Version.Should().Be("3", "the restart loads the NEWEST generation the record names");
    }

    [Fact(Timeout = 300_000)]
    public async Task AnUpdateArrivingWhileTheRestartIsInFlight_SchedulesNoSecondRestart()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(ct);
        await Land(ModuleSource(2, configuresMeshHub: true), "2", ct);
        var updater = new RecordingUpdater();
        await RunOneCheck(updater, ct, TimeSpan.FromHours(1));
        updater.Restarts.Should().Be(1);

        // The restart is in flight (rolled moments ago); another generation lands.
        await Land(ModuleSource(3, configuresMeshHub: true), "3", ct);
        var verdict = await RunOneCheck(updater, ct, TimeSpan.FromHours(1));

        updater.Restarts.Should().Be(1, "the in-flight restart loads whatever the record names — never a second one");
        verdict.Should().Contain("deferring the restart");
    }

    // ═════════════════════════════════════════ the negative control

    /// <summary>
    /// 🚨 THE NEGATIVE CONTROL: suppress the fallback — no self-update check runs after a landing
    /// that cannot go live — and the module stays landed-not-loaded. The guard the positive tests end
    /// with must then FAIL, naming the stuck module; if it did not, its silence in those tests would
    /// prove nothing.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task WithTheFallbackSuppressed_TheStuckLandedNotLoadedModuleIsNamed()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(ct);
        await Land(ModuleSource(2, configuresMeshHub: true), "2", ct);

        var guard = () => AssertNothingStuck();

        guard.Should().Throw<InvalidOperationException>("nothing activated the landed generation")
            .WithMessage($"*{Module}*");
    }

    // ═════════════════════════════════════════ harness

    /// <summary>Fails naming every module that has landed and is not serving in this process.</summary>
    private void AssertNothingStuck()
    {
        var report = Mesh.ServiceProvider.GetRequiredService<PendingModuleActivations>().Read();
        if (report.IsUndetermined)
            throw new InvalidOperationException("the activation state could not be read: " + report.UndeterminedReason);
        if (report.HasPending)
            throw new InvalidOperationException(
                "landed but not loaded in the running process (no restart and no live swap activated it): "
                + string.Join(", ", report.Pending.Select(p => p.Name)));
    }

    private async Task Land(string source, string version, CancellationToken ct) =>
        await Landing.LandModule(Module, [(Module + ".dll", Emit(source))], version: version)
            .Timeout(Budget).Await(ct);

    private async Task<string?> NameAt(string path, CancellationToken ct) =>
        (await ReadNode(path).Timeout(Budget).Await(ct))?.Name;

    private static string ModuleSource(int version, bool throwingNodes = false, bool configuresMeshHub = false) => $$"""
        [assembly: MeshWeaver.Test.GoesLive.Module]
        namespace MeshWeaver.Test.GoesLive;
        public sealed class ModuleAttribute : MeshWeaver.Mesh.MeshNodeProviderAttribute
        {
            public override System.Collections.Generic.IEnumerable<MeshWeaver.Mesh.MeshNode> Nodes =>
                {{(throwingNodes
                    ? "throw new System.InvalidOperationException(\"contributions of this generation cannot be built\")"
                    : "[new MeshWeaver.Mesh.MeshNode(\"" + ProbePath + "\") { Name = \"v" + version + "\", NodeType = \"Markdown\" }]")}};
            {{(configuresMeshHub
                ? "public override System.Collections.Generic.IEnumerable<System.Func<MeshWeaver.Messaging.MessageHubConfiguration, MeshWeaver.Messaging.MessageHubConfiguration>> HubConfigurations => [c => c with { }];"
                : "")}}
        }
        """;

    private static byte[] Emit(string source)
    {
        var compilation = CSharpCompilation.Create(
            Module,
            [CSharpSyntaxTree.ParseText(source)],
            PlatformReferences.Platform(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var buffer = new MemoryStream();
        var result = compilation.Emit(buffer);
        result.Success.Should().BeTrue(string.Join(Environment.NewLine,
            result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return buffer.ToArray();
    }

    private sealed class FakeAcrTagLister(IReadOnlyList<string> tags) : IAcrTagLister
    {
        public Task<IReadOnlyList<string>> ListTagsAsync(string repository, CancellationToken ct) =>
            Task.FromResult(tags);
    }

    /// <summary>The k8s seam: records every restart, and a restart IS a roll for the floor.</summary>
    private sealed class RecordingUpdater : IDeploymentUpdater
    {
        private int restarts;
        private long lastRolledTicks;

        public int Restarts => Volatile.Read(ref restarts);
        public bool CanPatch => true;

        public Task<DateTimeOffset?> LastRolledAtAsync(CancellationToken ct)
        {
            var ticks = Interlocked.Read(ref lastRolledTicks);
            return Task.FromResult<DateTimeOffset?>(ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero));
        }

        public Task PatchToVersionAsync(string versionTag, CancellationToken ct) => Task.CompletedTask;

        public Task<bool> RestartAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref restarts);
            Interlocked.Exchange(ref lastRolledTicks, DateTimeOffset.UtcNow.UtcTicks);
            return Task.FromResult(true);
        }
    }

    private sealed class AlwaysAvailable(IMessageHub hub, IConfiguration configuration)
        : ReleaseAvailabilityService(hub, configuration)
    {
        public override IObservable<UpdatabilityVerdict> IsUpdatable(string? targetVersion) =>
            Observable.Return(UpdatabilityVerdict.NotEnforced("this test host consumes no CI bakes"));
    }

    private sealed class SeamedSelfUpdateService(
        IMessageHub hub, IAcrTagLister acr, IDeploymentUpdater updater, SelfUpdateOptions options,
        ILogger<SelfUpdateHostedService>? logger, ReleaseAvailabilityService gate)
        : SelfUpdateHostedService(hub, acr, updater, options, logger)
    {
        public IObservable<System.Reactive.Unit> Evaluations => ChecksReported;

        protected override ReleaseAvailabilityService? ResolveAvailabilityGate() => gate;

        protected override ComboVerificationGate? ResolveComboGate() => null;
    }

    private async Task<string> RunOneCheck(IDeploymentUpdater updater, CancellationToken ct, TimeSpan? floor = null)
    {
        var options = new SelfUpdateOptions
        {
            RetryInterval = TimeSpan.FromMilliseconds(500),
            EventCoalesceWindow = TimeSpan.FromMilliseconds(50),
            DefaultPolicy = UpdatePolicyKind.Continuous,
            DefaultPattern = "*-ci*",
            MinRollInterval = floor ?? TimeSpan.Zero,
        };
        var service = new SeamedSelfUpdateService(
            Mesh, new FakeAcrTagLister(NothingNewer), updater, options,
            Mesh.ServiceProvider.GetService<ILogger<SelfUpdateHostedService>>(),
            new AlwaysAvailable(Mesh, new ConfigurationBuilder().Build()));
        var before = DateTimeOffset.UtcNow;
        await service.StartAsync(CancellationToken.None);
        try
        {
            await service.Evaluations.FirstAsync().Timeout(Budget).Await(ct);
            return await Observable.Create<UpdatePolicyContent>(observer =>
                {
                    using (Access.ImpersonateAsSystem())
                        return Mesh.GetWorkspace()
                            .GetMeshNodeStream(UpdatePolicyNodeType.NodePath)
                            .Where(node => node is not null)
                            .Select(node => UpdatePolicyNodeType.Parse(node, Mesh.JsonSerializerOptions))
                            .Subscribe(observer);
                })
                .Where(c => c.LastCheckedAt >= before)
                .Select(c => c.LastCheckVerdict)
                .OfType<string>()
                .FirstAsync()
                .Timeout(Budget)
                .Await(ct);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private Task Seed(CancellationToken ct)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var node = new MeshNode(UpdatePolicyNodeType.NodeId, UpdatePolicyNodeType.AdminPartition)
        {
            NodeType = UpdatePolicyNodeType.NodeType,
            Name = "Update Policy",
            State = MeshNodeState.Active,
            Content = new UpdatePolicyContent { Policy = UpdatePolicyKind.Continuous, Pattern = "*-ci*" },
        };
        return Observable.Create<MeshNode>(observer =>
            {
                using (Access.ImpersonateAsSystem())
                    return (IDisposable)meshService.CreateNode(node).Subscribe(observer);
            })
            .FirstAsync()
            .Timeout(Budget)
            .Await(ct);
    }
}
