using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.SelfUpdate;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
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
/// 🚨 <b>MeshWeaver#6829 — a landed module set is not left unloaded because a platform roll was
/// handed over.</b> Measured on memex.systemorph.com 2026-10-16: three replicas booted onto module set
/// 1986, set 1987 was proposed at 08:38:57Z, and every check those replicas ran from then on was a
/// <c>BuildCompletion</c> that found a newer platform build and HANDED it to the control lane. A
/// hand-over skipped the module half of the check ENTIRELY — not only the restart (which would race the
/// routed Roll) but the live swap too, which races nothing. So the replicas reported "20 module(s) are
/// landed but not yet loaded" until the roll re-created them; behind a roll that waits for an approval
/// they would have reported it indefinitely.
///
/// <para>The arrangement is the production one with only the IO seams faked (the registry listing, the
/// k8s updater, the control inbox): an instance on the control lane RUNS module M at generation N, the
/// registry lists a newer platform build, and generation N+1 lands through the real
/// <see cref="ModuleLandingService.LandModule"/>. One check runs.</para>
/// <list type="bullet">
/// <item>a live-updatable M goes LIVE in the process, the release is still handed over, and NO
/// restart is announced (the roll is the restart);</item>
/// <item>an M that cannot be swapped in-process keeps N serving and announces NO separate restart —
/// the verdict names the handed-over roll as what activates it.</item>
/// </list>
/// <para>Negative control: the same check with NO newer platform build takes the module half as it
/// always did — which is what makes the first bullet a statement about the hand-over and not about
/// the live updater.</para>
/// </summary>
public sealed class ModulesGoLiveWhileARollIsHandedOverTest : MonolithMeshTestBase
{
    private const string Module = "MeshWeaver.Test.HandedOverGoesLive";
    private const string ProbePath = "HandedOverGoesLiveProbe";
    private const string CandidateTag = "9999.0.0-ci.1";
    private const string Deployment = "unit-fleet-instance";
    private const string InboxUrl = "https://control.example/api/hooks/Hosting/PlatformBuilds";
    private const string Secret = "unit-control-inbox-secret";
    private static TimeSpan Budget => TestTimeouts.Convergence;

    private static string Installed => ShippedReleaseSeed.InstalledPlatformVersion;
    private static string[] NewerPlatform => ["1.9.0-ci.0", Installed.Split('+')[0], CandidateTag];
    private static string[] NothingNewer => ["1.9.0-ci.0", Installed.Split('+')[0]];

    private readonly string root = Path.Combine(Path.GetTempPath(), "mw-6829-" + Guid.NewGuid().ToString("N"));
    private string? bootEntry;

    public ModulesGoLiveWhileARollIsHandedOverTest(ITestOutputHelper output) : base(output)
    {
    }

    /// <summary>The instance RUNNING M@N, landed and loaded at boot — before the base builds the mesh.</summary>
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
                // The secret the hand-over signs with is read off the mesh's configuration at
                // delivery time, exactly as in production.
                services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?> { [SelfUpdateHandover.SecretKey] = Secret })
                    .Build());
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

    [Fact(Timeout = 300_000)]
    public async Task ALiveUpdatableModule_GoesLive_WhileThePlatformRollIsHandedOver()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(ct);
        (await NameAt(ProbePath, ct)).Should().Be("v1", "the arrangement: the instance runs M@N");

        await Land(ModuleSource(2), "2", ct);
        var inbox = new FakeControlInbox();
        var updater = new RecordingUpdater();
        var verdict = await RunOneCheck(updater, inbox, NewerPlatform, ct);

        verdict.Should().Contain("handed to the control lane", "the arrangement: a newer platform build was handed over");
        verdict.Should().Contain("went LIVE", "a hand-over is no reason to leave a swappable module unloaded");
        (await NameAt(ProbePath, ct)).Should().Be("v2", "M@N+1 serves in the running process");
        inbox.Events.Should().Equal(new[] { SelfUpdateHandover.ReleaseEvent },
            "the release is handed over and NO restart is announced — the roll is the restart");
        updater.Restarts.Should().Be(0);
        updater.Tags.Should().BeEmpty("a control-lane install never patches itself");
        AssertNothingStuck();
    }

    /// <summary>
    /// The activation marker outlives a successful live swap until the next boot, so every LATER
    /// handed-over check still finds it set — with nothing pending in this process. That check must
    /// say nothing is pending here (still <see cref="SelfUpdateOutcome.HandedOver"/>), never that an
    /// activation is still pending, and must announce nothing.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task ALaterHandedOverCheck_AfterTheLiveSwap_SaysNothingIsPendingHere()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(ct);
        await Land(ModuleSource(2), "2", ct);
        var first = await RunOneCheck(new RecordingUpdater(), new FakeControlInbox(), NewerPlatform, ct);
        first.Should().Contain("went LIVE", "the arrangement: the first handed-over check swapped M@N+1 in");
        AssertNothingStuck();

        var inbox = new FakeControlInbox();
        var updater = new RecordingUpdater();
        var verdict = await RunOneCheck(updater, inbox, NewerPlatform, ct);

        verdict.Should().Contain("handed to the control lane", "the outcome is still the hand-over");
        verdict.Should().Contain("nothing landed is pending in this process",
            "the live swap already happened — the marker alone is not a pending activation");
        verdict.Should().NotContain("A landed module generation is pending activation", "nothing is pending in this process");
        verdict.Should().NotContain("went LIVE");
        inbox.Events.Should().Equal(new[] { SelfUpdateHandover.ReleaseEvent });
        updater.Restarts.Should().Be(0);
        (await NameAt(ProbePath, ct)).Should().Be("v2");
    }

    [Fact(Timeout = 300_000)]
    public async Task ARestartRequiredModule_WaitsForTheHandedOverRoll_AndNoSecondRestartIsAnnounced()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(ct);
        await Land(ModuleSource(2, configuresMeshHub: true), "2", ct);
        var inbox = new FakeControlInbox();
        var updater = new RecordingUpdater();
        var verdict = await RunOneCheck(updater, inbox, NewerPlatform, ct);

        inbox.Events.Should().Equal(new[] { SelfUpdateHandover.ReleaseEvent },
            "a restart request would only race the routed Roll the hand-over opened");
        verdict.Should().Contain("activated by the handed-over roll",
            "the verdict names what activates the module that could not go live");
        (await NameAt(ProbePath, ct)).Should().Be("v1", "N keeps serving until the roll — never a half-swapped state");
    }

    /// <summary>
    /// The negative control: no newer platform build, nothing handed over — the module half runs as it
    /// always did and the module goes live. Together with the first test this pins the hand-over as the
    /// variable: before #6829's fix, that test's check skipped the live swap and M@N kept serving.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task WithNothingHandedOver_TheModuleHalfRunsAsBefore()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(ct);
        await Land(ModuleSource(2), "2", ct);
        var inbox = new FakeControlInbox();
        var verdict = await RunOneCheck(new RecordingUpdater(), inbox, NothingNewer, ct);

        verdict.Should().Contain("no newer release").And.Contain("went LIVE");
        inbox.Events.Should().BeEmpty("nothing to hand over, and nothing needed a restart");
        (await NameAt(ProbePath, ct)).Should().Be("v2");
    }

    // ═════════════════════════════════════════ harness

    private void AssertNothingStuck()
    {
        var report = Mesh.ServiceProvider.GetRequiredService<PendingModuleActivations>().Read();
        if (report.IsUndetermined)
            throw new InvalidOperationException("the activation state could not be read: " + report.UndeterminedReason);
        if (report.HasPending)
            throw new InvalidOperationException(
                "landed but not loaded in the running process: " + string.Join(", ", report.Pending.Select(p => p.Name)));
    }

    private async Task Land(string source, string version, CancellationToken ct) =>
        await Landing.LandModule(Module, [(Module + ".dll", Emit(source))], version: version)
            .Timeout(Budget).Await(ct);

    private async Task<string?> NameAt(string path, CancellationToken ct) =>
        (await ReadNode(path).Timeout(Budget).Await(ct))?.Name;

    private static string ModuleSource(int version, bool configuresMeshHub = false) => $$"""
        [assembly: MeshWeaver.Test.HandedOverGoesLive.Module]
        namespace MeshWeaver.Test.HandedOverGoesLive;
        public sealed class ModuleAttribute : MeshWeaver.Mesh.MeshNodeProviderAttribute
        {
            public override System.Collections.Generic.IEnumerable<MeshWeaver.Mesh.MeshNode> Nodes =>
                [new MeshWeaver.Mesh.MeshNode("{{ProbePath}}") { Name = "v{{version}}", NodeType = "Markdown" }];
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
        return StandInCompile.Emit(compilation);
    }

    /// <summary>The network, substituted: records the event of every delivery and accepts it, verified.</summary>
    private sealed class FakeControlInbox : HttpMessageHandler
    {
        private ImmutableList<string> events = ImmutableList<string>.Empty;

        public ImmutableList<string> Events => events;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var name = doc.RootElement.GetProperty("event").GetString() ?? "";
            ImmutableInterlocked.Update(ref events, current => current.Add(name));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":\"accepted\",\"signature\":\"verified\"}"),
            };
        }
    }

    private sealed class SeamedHandover(IMessageHub hub, HttpMessageHandler inbox)
        : SelfUpdateHandover(hub, null, new HttpClient(inbox))
    {
        public override Settings ReadSettings() =>
            new(Deployment, InboxUrl, SecretPresent: true, LocalTargetListed: false, LocalSecretPresent: false,
                "https://unit.example");
    }

    private sealed class FakeAcrTagLister(IReadOnlyList<string> tags) : IAcrTagLister
    {
        public Task<IReadOnlyList<string>> ListTagsAsync(string repository, CancellationToken ct) =>
            Task.FromResult(tags);
    }

    /// <summary>The k8s seam: an updater that COULD patch, so only the chart's declaration decides.</summary>
    private sealed class RecordingUpdater : IDeploymentUpdater
    {
        private ImmutableList<string> tags = ImmutableList<string>.Empty;
        private int restarts;

        public ImmutableList<string> Tags => tags;
        public int Restarts => Volatile.Read(ref restarts);
        public bool CanPatch => true;

        public Task<DateTimeOffset?> LastRolledAtAsync(CancellationToken ct) => Task.FromResult<DateTimeOffset?>(null);

        public Task PatchToVersionAsync(string versionTag, CancellationToken ct)
        {
            ImmutableInterlocked.Update(ref tags, current => current.Add(versionTag));
            return Task.CompletedTask;
        }

        public Task<bool> RestartAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref restarts);
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
        ILogger<SelfUpdateHostedService>? logger, ReleaseAvailabilityService gate, SelfUpdateHandover handover)
        : SelfUpdateHostedService(hub, acr, updater, options, logger)
    {
        public IObservable<System.Reactive.Unit> Evaluations => ChecksReported;

        protected override ReleaseAvailabilityService? ResolveAvailabilityGate() => gate;

        protected override ComboVerificationGate? ResolveComboGate() => null;

        protected override SelfUpdateHandover ResolveHandover() => handover;
    }

    private async Task<string> RunOneCheck(IDeploymentUpdater updater, FakeControlInbox inbox, string[] registry, CancellationToken ct)
    {
        var options = new SelfUpdateOptions
        {
            RetryInterval = TimeSpan.FromMilliseconds(500),
            EventCoalesceWindow = TimeSpan.FromMilliseconds(50),
            DefaultPolicy = UpdatePolicyKind.Continuous,
            DefaultPattern = "*-ci*",
            MinRollInterval = TimeSpan.Zero,
            CanPatch = false,
            Registry = "cr.example",
        };
        var service = new SeamedSelfUpdateService(
            Mesh, new FakeAcrTagLister(registry), updater, options,
            Mesh.ServiceProvider.GetService<ILogger<SelfUpdateHostedService>>(),
            new AlwaysAvailable(Mesh, new ConfigurationBuilder().Build()),
            new SeamedHandover(Mesh, inbox));
        var before = DateTimeOffset.UtcNow;
        // 🚨 Subscribe BEFORE StartAsync: Await subscribes on call, ChecksReported is a non-replaying
        // Subject, and StartAsync schedules the startup check on the task pool and returns at once — a
        // check that finished before a later subscription would be lost and the wait would time out.
        var evaluated = service.Evaluations.FirstAsync().Timeout(Budget).Await(ct);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await evaluated;
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
