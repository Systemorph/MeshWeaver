#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
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
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>A module swap is a restart, and the restart HAPPENS (#3650) — the poller half, against a
/// real monolith mesh.</b>
///
/// <para>A landed module generation loads only at a restart (restart-as-activation). Until this
/// change the restart was whatever platform roll came next: a module could ship, land, raise
/// <c>PendingRestart</c> and sit unloaded for days behind a fleet with nothing newer to roll to —
/// which is the opposite of the maintainer's rule that a module version that ships is in use. The
/// self-updater now treats a pending restart like a roll of the image it runs: after the platform
/// half of every check, when that half patched nothing, it reads the activation record and — paced
/// by the same <c>MinRollInterval</c> floor as any roll — asks the deployment updater to restart the
/// workloads on the same image.</para>
///
/// <para><b>Fails on unfixed code:</b> <see cref="APendingRestart_RollsTheRunningImage"/> sees an
/// updater that was never asked to restart and a verdict reading only "no newer release".</para>
///
/// <para>Only the documented IO seams are faked — the ACR listing, the k8s patcher, and the module
/// root the landing service reads (a temp directory carrying the real marker file). The hub, the
/// workspace, the policy node, every <c>stream.Update</c> and the whole decision path are real.</para>
/// </summary>
public class SelfUpdatePendingRestartTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static TimeSpan Budget => TestTimeouts.Convergence;

    private static string Installed => ShippedReleaseSeed.InstalledPlatformVersion;

    private static string InstalledTag => Installed.Split('+')[0];

    /// <summary>Two releases behind this install plus the tag it runs — a registry with nothing
    /// newer, in which the installed image still resolves. The premise is asserted per scenario.</summary>
    private static string[] NothingNewer => ["1.9.0-ci.0", "2.0.0-ci.0", InstalledTag];

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddUpdatePolicyType().AddGitHubSyncTypes();

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    // ══════════════════════════════════════════════════════════════════════════
    //  The restart, and the rules it obeys
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 🚨 <b>The acceptance criterion.</b> Nothing newer is in the registry, the module activation
    /// record says a restart is pending, and the install never rolled: the check restarts the
    /// workloads on the image they run, patches NO image, and says so on the policy node.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task APendingRestart_RollsTheRunningImage()
    {
        AssertBehindInstalled(NothingNewer.Take(2));
        await Seed(UpdatePolicyKind.Continuous);
        using var root = ModuleRoot.WithPendingRestart();
        var updater = new RecordingUpdater { LastRolledAt = null };

        var content = await RunOneCheck(updater, root.Landing, NothingNewer);

        updater.Restarts.Should().Be(1,
            "a landed module generation loads only at a restart, and the restart must not wait for "
            + "an unrelated platform roll");
        updater.Tags.Should().BeEmpty("nothing newer exists — the image stays, only the pods roll");
        content.LastCheckVerdict.Should().Contain("no newer release",
            "the platform half of the check still has to say what it found");
        content.LastCheckVerdict.Should().Contain($"RESTARTED on {Installed}",
            "the restart is the durable second sentence of the same verdict");
    }

    /// <summary>
    /// 🚨 A restart is a roll — it drops the same live circuits — so the pacing floor applies to it
    /// exactly as to a roll: an install that rolled five minutes ago, inside a one-hour floor,
    /// restarts nothing now and says the next check re-decides it.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task APendingRestart_InsideTheRollFloor_IsDeferred()
    {
        await Seed(UpdatePolicyKind.Continuous);
        using var root = ModuleRoot.WithPendingRestart();
        var updater = new RecordingUpdater { LastRolledAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5) };

        var content = await RunOneCheck(updater, root.Landing, NothingNewer,
            options => options with { MinRollInterval = TimeSpan.FromHours(1) });

        updater.Restarts.Should().Be(0, "the floor defers a restart exactly as it defers a roll");
        content.LastCheckVerdict.Should().Contain("deferring the restart");
    }

    /// <summary>The control: no pending restart, no restart — and the verdict is the plain
    /// up-to-date sentence, untouched.</summary>
    [Fact(Timeout = 240_000)]
    public async Task NoPendingRestart_RestartsNothing()
    {
        await Seed(UpdatePolicyKind.Continuous);
        using var root = ModuleRoot.Clean();
        var updater = new RecordingUpdater { LastRolledAt = null };

        var content = await RunOneCheck(updater, root.Landing, NothingNewer);

        updater.Restarts.Should().Be(0);
        content.LastCheckVerdict.Should().Contain("no newer release");
        content.LastCheckVerdict.Should().NotContain("RESTARTED");
    }

    /// <summary>
    /// 🚨 An updater that predates the restart seam cannot restart on the same image (a same-tag
    /// image patch rolls nothing on Kubernetes). That is a state an operator has to see: the
    /// verdict names it and names the move, instead of folding it into "no newer release".
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task AnUpdaterWithoutTheRestartSeam_ReportsTheRestartAsUnavailable()
    {
        await Seed(UpdatePolicyKind.Continuous);
        using var root = ModuleRoot.WithPendingRestart();
        var updater = new LegacyUpdater();

        var content = await RunOneCheck(updater, root.Landing, NothingNewer);

        content.LastCheckVerdict.Should().Contain("cannot restart itself");
        content.LastCheckVerdict.Should().Contain("kubectl rollout restart",
            "an unactionable pending restart is a module that never loads, with everything green");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Harness
    // ══════════════════════════════════════════════════════════════════════════

    private static void AssertBehindInstalled(IEnumerable<string> tags)
    {
        foreach (var tag in tags)
            VersionSelect.IsNewer(tag, Installed).Should().BeFalse(
                $"the scenario needs {tag} to be BEHIND the running {Installed}; otherwise the check "
                + "would roll forward and never reach the restart decision under test");
    }

    /// <summary>A module root in a temp directory, carrying the REAL marker the landing lane
    /// raises (<c>modules/activation.d/.pending-restart</c>) — or not.</summary>
    private sealed class ModuleRoot : IDisposable
    {
        private readonly string directory;

        public ModuleLandingService Landing { get; }

        private ModuleRoot(bool pendingRestart)
        {
            directory = Path.Combine(Path.GetTempPath(), "mw-3650-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            if (pendingRestart)
                ModuleActivationSidecar.SetPendingRestart(directory, true);
            Landing = new ModuleLandingService(logger: null, baseDirectory: directory);
        }

        public static ModuleRoot WithPendingRestart() => new(pendingRestart: true);

        public static ModuleRoot Clean() => new(pendingRestart: false);

        public void Dispose()
        {
            Landing.Dispose();
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // A temp directory that outlives the test is harmless.
            }
        }
    }

    private sealed class FakeAcrTagLister(IReadOnlyList<string> tags) : IAcrTagLister
    {
        public Task<IReadOnlyList<string>> ListTagsAsync(string repository, CancellationToken ct) =>
            Task.FromResult(tags);
    }

    /// <summary>The k8s seam, recording every image patch and every same-image restart.</summary>
    private sealed class RecordingUpdater : IDeploymentUpdater
    {
        private ImmutableList<string> tags = ImmutableList<string>.Empty;
        private int restarts;

        public ImmutableList<string> Tags => tags;
        public int Restarts => Volatile.Read(ref restarts);
        public DateTimeOffset? LastRolledAt { get; init; }
        public bool CanPatch => true;

        public Task<DateTimeOffset?> LastRolledAtAsync(CancellationToken ct) => Task.FromResult(LastRolledAt);

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

    /// <summary>An updater compiled before the seam: the interface default answers "cannot".</summary>
    private sealed class LegacyUpdater : IDeploymentUpdater
    {
        public bool CanPatch => true;

        public Task<DateTimeOffset?> LastRolledAtAsync(CancellationToken ct) => Task.FromResult<DateTimeOffset?>(null);

        public Task PatchToVersionAsync(string versionTag, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class AlwaysAvailable(IMessageHub hub, IConfiguration configuration)
        : ReleaseAvailabilityService(hub, configuration)
    {
        public override IObservable<UpdatabilityVerdict> IsUpdatable(string? targetVersion) =>
            Observable.Return(UpdatabilityVerdict.NotEnforced("this test host consumes no CI bakes"));
    }

    /// <summary>The poller with the three seams supplied directly: the availability gate, no combo
    /// gate, and the module root under test.</summary>
    private sealed class SeamedSelfUpdateService(
        IMessageHub hub,
        IAcrTagLister acr,
        IDeploymentUpdater updater,
        SelfUpdateOptions options,
        ILogger<SelfUpdateHostedService>? logger,
        ReleaseAvailabilityService gate,
        ModuleLandingService landing)
        : SelfUpdateHostedService(hub, acr, updater, options, logger)
    {
        public IObservable<Unit> Evaluations => ChecksReported;

        protected override ReleaseAvailabilityService? ResolveAvailabilityGate() => gate;

        protected override ComboVerificationGate? ResolveComboGate() => null;

        protected override ModuleLandingService? ResolveLandingService() => landing;
    }

    private static SelfUpdateOptions FastPoll() => new()
    {
        RetryInterval = TimeSpan.FromMilliseconds(500),
        EventCoalesceWindow = TimeSpan.FromMilliseconds(50),
        DefaultPolicy = UpdatePolicyKind.Continuous,
        DefaultPattern = "*-ci*",
    };

    private async Task<UpdatePolicyContent> RunOneCheck(
        IDeploymentUpdater updater, ModuleLandingService landing, IReadOnlyList<string> registry,
        Func<SelfUpdateOptions, SelfUpdateOptions>? configure = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var options = configure?.Invoke(FastPoll()) ?? FastPoll();
        var service = new SeamedSelfUpdateService(
            Mesh, new FakeAcrTagLister(registry), updater, options,
            Mesh.ServiceProvider.GetService<ILogger<SelfUpdateHostedService>>(),
            new AlwaysAvailable(Mesh, new ConfigurationBuilder().Build()),
            landing);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await service.Evaluations.FirstAsync().Timeout(Budget).Await(ct);
            return await WaitForContent(c => c.LastCheckVerdict is not null);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private Task Seed(UpdatePolicyKind policy)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var node = new MeshNode(UpdatePolicyNodeType.NodeId, UpdatePolicyNodeType.AdminPartition)
        {
            NodeType = UpdatePolicyNodeType.NodeType,
            Name = "Update Policy",
            State = MeshNodeState.Active,
            // 2026-09-08: a continuous build is eligible only when a pattern admits it.
            Content = new UpdatePolicyContent
            {
                Policy = policy,
                Pattern = policy == UpdatePolicyKind.Continuous ? "*-ci*" : null,
            },
        };
        return Observable.Create<MeshNode>(observer =>
            {
                using (Access.ImpersonateAsSystem())
                    return (IDisposable)meshService.CreateNode(node).Subscribe(observer);
            })
            .FirstAsync()
            .Timeout(Budget)
            .Await(TestContext.Current.CancellationToken);
    }

    private Task<UpdatePolicyContent> WaitForContent(Func<UpdatePolicyContent, bool> predicate) =>
        Observable.Create<UpdatePolicyContent>(observer =>
            {
                using (Access.ImpersonateAsSystem())
                    return Mesh.GetWorkspace()
                        .GetMeshNodeStream(UpdatePolicyNodeType.NodePath)
                        .Where(node => node is not null)
                        .Select(node => UpdatePolicyNodeType.Parse(node, Mesh.JsonSerializerOptions))
                        .Subscribe(observer);
            })
            .Where(predicate)
            .FirstAsync()
            .Timeout(Budget)
            .Await(TestContext.Current.CancellationToken);
}
