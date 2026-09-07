#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
/// 🚨 <b>An install whose own tag was withdrawn is STRANDED, not up to date (#3543) — the poller
/// half, against a real monolith mesh.</b>
///
/// <para>On 2026-09-07 both AKS portals rolled themselves onto <c>3.1.0-ci.7841</c> — a version line
/// published on 2026-09-05 from a reverted bump and older in content than the live <c>3.0.0-ci.79xx</c>
/// line — and then could not leave. The 3.1.0 tags were untagged from ACR, so the image the
/// Deployments named no longer existed and no new pod could start from it. The self-updater kept
/// reporting <i>"no newer release: 500 tag(s) listed, none newer than the installed
/// 3.1.0-ci.7841"</i>, which is the SAME sentence a perfectly current install prints; both portals
/// had to be moved by an operator <c>kubectl set image</c>.</para>
///
/// <para>It was unrecoverable by construction, not by bad luck: the only question the check asked was
/// "is anything newer than what I run", and nothing is ever newer than a tag that already outranks
/// everything left. So the check now asks a SECOND question of the listing it has already fetched —
/// does the tag I run still resolve? — and treats the three answers as three answers.</para>
///
/// <para><b>Fails on unfixed code:</b> <see cref="AWithdrawnInstalledTag_RollsToTheBestAvailableRelease"/>
/// sees an unpatched updater and a verdict reading "no newer release", exactly as production did.</para>
///
/// <para>Only the two documented IO seams are faked — the ACR listing and the k8s PATCH. The hub, the
/// workspace, the policy node, the live stream, every <c>stream.Update</c> and the whole decision
/// path are real.</para>
/// </summary>
public class SelfUpdateStrandRecoveryTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>🚨 <see cref="TestTimeouts.Convergence"/>, never a literal: a hand-written 30 s is
    /// both a guess about machine speed AND the framework's own write bound (#2819).</summary>
    private static TimeSpan Budget => TestTimeouts.Convergence;

    /// <summary>
    /// What this test host reports as its running platform version — <c>3.0.0+&lt;sha&gt;</c> on a CI
    /// build, <c>3.0.0-ci.0</c> locally. The scenarios are built RELATIVE to it rather than around a
    /// hard-coded string, and each one asserts its own premise (see <see cref="AssertBehindInstalled"/>),
    /// so a future change to how a build stamps itself fails this file loudly instead of quietly
    /// turning it into a test of nothing.
    /// </summary>
    private static string Installed => ShippedReleaseSeed.InstalledPlatformVersion;

    /// <summary>The installed version as a REGISTRY TAG would spell it: the running
    /// <c>InformationalVersion</c> carries <c>+build.&lt;ticks&gt;</c> / <c>+&lt;sha&gt;</c> build
    /// metadata that no image tag has.</summary>
    private static string InstalledTag => Installed.Split('+')[0];

    /// <summary>
    /// Two published releases, both strictly BEHIND this install in CD lineage and in SemVer, and
    /// neither of them the tag this install runs — the registry a withdrawal leaves behind. The
    /// newest of them, <c>2.0.0-ci.0</c>, is the only thing a stranded install can recover to.
    /// </summary>
    private static readonly string[] WithoutTheInstalledTag = ["1.9.0-ci.0", "2.0.0-ci.0"];

    private const string RecoveryTarget = "2.0.0-ci.0";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        // AddGitHubSyncTypes registers the BuildCompletion satellite the self-update watch reacts to
        // — types only, the same production registration rather than a duplicate declaration.
        => base.ConfigureMesh(builder).AddUpdatePolicyType().AddGitHubSyncTypes();

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    // ══════════════════════════════════════════════════════════════════════════
    //  The strand, and the way out
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 🚨 <b>The acceptance criterion.</b> Nothing in the registry is newer, and the tag this install
    /// runs is not in the registry at all. That is not "up to date" — the workloads name an image no
    /// pod can start from — so the check takes the best AVAILABLE release, backwards in lineage, and
    /// says on the policy node that it did.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task AWithdrawnInstalledTag_RollsToTheBestAvailableRelease()
    {
        AssertBehindInstalled(WithoutTheInstalledTag);
        await Seed(UpdatePolicyKind.Continuous);
        var updater = new RecordingUpdater();

        var content = await RunOneCheck(updater, WithoutTheInstalledTag);

        updater.Tags.Should().Contain(RecoveryTarget,
            "an install pointing at an untagged image cannot start a new pod, and no publication can "
            + "ever rescue it by being 'newer' — the only way out is the best release that exists");
        content.UnresolvedInstalledTag.Should().Be(Installed,
            "the strand has to be VISIBLE on Admin/UpdatePolicy: a log line depends on a per-category "
            + "level a deployment may never have set, a node write does not");
        content.LastCheckVerdict.Should().Contain("RECOVERY",
            "a roll that went BACKWARDS must say why it did, or the next reader sees an unexplained "
            + "downgrade");
        content.LastCheckVerdict.Should().NotContain("no newer release",
            "printing the up-to-date sentence over a strand is the whole of #3543");
    }

    /// <summary>
    /// 🚨 The control, and the half that makes the recovery safe: with the installed tag PRESENT in
    /// the listing and nothing newer, the check must report exactly what it reported before and patch
    /// NOTHING. A recovery reachable from anything but a proven withdrawal would roll the whole fleet
    /// backwards on every check.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task ACurrentInstall_StillReportsNoNewerRelease_AndRollsNothing()
    {
        string[] registry = [.. WithoutTheInstalledTag, InstalledTag];
        AssertBehindInstalled(WithoutTheInstalledTag);
        await Seed(UpdatePolicyKind.Continuous);
        var updater = new RecordingUpdater();

        var content = await RunOneCheck(updater, registry);

        updater.Tags.Should().BeEmpty("nothing is newer and the installed image still exists");
        content.UnresolvedInstalledTag.Should().BeNull(
            "the field is written on EVERY check, so a healed strand disappears instead of lingering "
            + "as a stale scare");
        content.LastCheckVerdict.Should().Contain("no newer release",
            "the up-to-date sentence is still the right one when the install really is up to date");
    }

    /// <summary>
    /// 🚨 The terminal strand: the tag is gone AND the policy leaves nothing eligible to recover to
    /// (Stable considers only clean releases; this registry publishes none). Nothing in the process
    /// can fix that, so the verdict says so and names the operator's move — rather than printing the
    /// sentence that made both portals look healthy for hours.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task AStrandWithNothingToRecoverTo_SaysSo_InsteadOfLookingHealthy()
    {
        await Seed(UpdatePolicyKind.Stable);
        var updater = new RecordingUpdater();

        var content = await RunOneCheck(updater, WithoutTheInstalledTag);

        updater.Tags.Should().BeEmpty("there is no eligible release to roll to");
        content.UnresolvedInstalledTag.Should().Be(Installed);
        content.LastCheckVerdict.Should().Contain("STRANDED");
        content.LastCheckVerdict.Should().Contain("kubectl set image",
            "an unactionable strand is how an install stays down for hours with everything green");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Harness
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 🚨 The premise, asserted rather than assumed. Every scenario here depends on the listed tags
    /// being BEHIND what this host runs; if a future change to the build stamp made one of them look
    /// newer, the recovery path would never be reached and the tests above would pass having measured
    /// the ordinary update path instead. That is the shape of a verification step that cannot fail,
    /// so it is checked out loud.
    /// </summary>
    private static void AssertBehindInstalled(IEnumerable<string> tags)
    {
        foreach (var tag in tags)
            VersionSelect.IsNewer(tag, Installed).Should().BeFalse(
                $"the scenario needs {tag} to be BEHIND the running {Installed}; it is not, so this "
                + "test would exercise the ordinary update path and prove nothing about a strand. "
                + "Move the fixture tags down, do not delete the assertion.");
    }

    /// <summary>Fake registry (the documented IO seam): exactly the tags a scenario hands it.</summary>
    private sealed class FakeAcrTagLister(IReadOnlyList<string> tags) : IAcrTagLister
    {
        public Task<IReadOnlyList<string>> ListTagsAsync(string repository, CancellationToken ct) =>
            Task.FromResult(tags);
    }

    /// <summary>Fake k8s patcher (the documented IO seam) — records every applied tag.</summary>
    private sealed class RecordingUpdater : IDeploymentUpdater
    {
        private ImmutableList<string> tags = ImmutableList<string>.Empty;

        public ImmutableList<string> Tags => tags;
        public bool CanPatch => true;

        public Task<DateTimeOffset?> LastRolledAtAsync(CancellationToken ct) =>
            Task.FromResult<DateTimeOffset?>(null);

        public Task PatchToVersionAsync(string versionTag, CancellationToken ct)
        {
            ImmutableInterlocked.Update(ref tags, current => current.Add(versionTag));
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// The availability gate, pinned to "nothing to enforce here". The gates are INDEPENDENT of the
    /// selection this suite is about — leaving one free to decide would make every assertion here
    /// ambiguous about which decision produced the outcome.
    /// </summary>
    private sealed class AlwaysAvailable(IMessageHub hub, IConfiguration configuration)
        : ReleaseAvailabilityService(hub, configuration)
    {
        public override IObservable<UpdatabilityVerdict> IsUpdatable(string? targetVersion) =>
            Observable.Return(UpdatabilityVerdict.NotEnforced(
                "this test host consumes no CI bakes"));
    }

    /// <summary>The poller with the availability gate supplied directly and no combo gate — the same
    /// injection seams ComboGateRollTest uses, so the DECISION path under test is the production
    /// one.</summary>
    private sealed class SeamedSelfUpdateService(
        IMessageHub hub,
        IAcrTagLister acr,
        IDeploymentUpdater updater,
        SelfUpdateOptions options,
        ILogger<SelfUpdateHostedService>? logger,
        ReleaseAvailabilityService gate)
        : SelfUpdateHostedService(hub, acr, updater, options, logger)
    {
        public IObservable<Unit> Evaluations => ChecksReported;

        protected override ReleaseAvailabilityService? ResolveAvailabilityGate() => gate;

        protected override ComboVerificationGate? ResolveComboGate() => null;
    }

    private static SelfUpdateOptions FastPoll(UpdatePolicyKind policy) => new()
    {
        RetryInterval = TimeSpan.FromMilliseconds(500),
        EventCoalesceWindow = TimeSpan.FromMilliseconds(50),
        DefaultPolicy = policy,
    };

    /// <summary>
    /// Starts the poller, waits for ONE check to have been evaluated AND reported, then reads the
    /// policy content the check produced.
    ///
    /// <para>🚨 Waits for the service to have EVALUATED, never for a state to appear inside a bound:
    /// this service is event-driven, so "the state is not there yet" and "the first check has not run
    /// yet" are indistinguishable from outside, and on a loaded shard the second is what actually
    /// happens — reported as if it were a wrong verdict.</para>
    /// </summary>
    private async Task<UpdatePolicyContent> RunOneCheck(
        RecordingUpdater updater, IReadOnlyList<string> registry)
    {
        var ct = TestContext.Current.CancellationToken;
        var service = new SeamedSelfUpdateService(
            Mesh, new FakeAcrTagLister(registry), updater, FastPoll(UpdatePolicyKind.Continuous),
            Mesh.ServiceProvider.GetService<ILogger<SelfUpdateHostedService>>(),
            new AlwaysAvailable(Mesh, new ConfigurationBuilder().Build()));

        await service.StartAsync(CancellationToken.None);
        try
        {
            await service.Evaluations.FirstAsync().Timeout(Budget).Await(ct);
            // The check's own verdict stamp is the LAST write of a check, so a content carrying it
            // carries every earlier write of that check too.
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
            Content = new UpdatePolicyContent { Policy = policy },
        };
        // System scope opened/closed SYNCHRONOUSLY around the subscribe — impersonation is an
        // AsyncLocal, so Observable.Using would restore it on the wrong thread.
        return Observable.Create<MeshNode>(observer =>
            {
                using (Access.ImpersonateAsSystem())
                    return (IDisposable)meshService.CreateNode(node).Subscribe(observer);
            })
            .FirstAsync()
            .Timeout(Budget)
            .Await(TestContext.Current.CancellationToken);
    }

    /// <summary>The first reconciled content matching <paramref name="predicate"/> — the
    /// wait-on-the-condition read, never a bare first emission (which can predate the write).</summary>
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
