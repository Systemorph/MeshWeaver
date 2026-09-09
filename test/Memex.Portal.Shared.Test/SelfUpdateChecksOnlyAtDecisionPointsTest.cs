#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
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
/// 🚨 <b>#3790 — "Self-update check runs on every build completion on every replica under
/// UpdatePolicy=None, and the replicas fight over Admin/UpdatePolicy.lastCheckedAt".</b>
///
/// <para>Measured on memex 2026-09-09, 01:30–06:36Z: <b>158</b> checks in 5 h, every one of them
/// reporting <c>"updates are disabled on this install (Admin/UpdatePolicy = None)"</c> — a verdict
/// the record already carried — and every one of them writing <c>LastCheckedAt</c> cross-hub into
/// the same leaf from both replicas. 14 <c>[MergeGuard] refused stale/reordered</c>, 10
/// <c>OWNER_NACK_REENQUEUE</c>, five <c>STALE_MIRROR</c>, three <c>ADVANCE_WITHOUT_HANDOFF</c> and
/// two ten-second <c>[UpdateQueue] FAILED</c> stalls in the hub that also carries user writes, with
/// <c>Admin/UpdatePolicy</c> standing at <b>version 62,671</b>.</para>
///
/// <para>The cause is a rate, and the rate is not this install's: <c>BuildCompletion</c> ticks once
/// per publication ANYWHERE in the fleet. Under <c>None</c> such a check is decision-free by
/// construction — <see cref="SelfUpdateVerdict.MayRestartAfter"/> is <c>false</c> for
/// <c>UpdatesDisabled</c>, so it cannot roll and cannot restart either.</para>
///
/// <para>🚨 <b>The line these tests walk:</b> #2553 removed a <c>Where</c> that dropped every check
/// under <c>None</c> and left an admin-pinned install indistinguishable from a broken one. So the
/// suite asserts BOTH directions — the fleet-paced triggers stop, and the record still carries the
/// disabled verdict, still moves on the safety net, and still wakes on a policy change.</para>
///
/// <para><b>Fails on unfixed code:</b> <see cref="UnderNone_ABuildCompletion_IsNotACheck"/> sees the
/// second check its control proves the driver is capable of.</para>
/// </summary>
public class SelfUpdateChecksOnlyAtDecisionPointsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static TimeSpan Budget => TestTimeouts.Convergence;

    private static string Installed => ShippedReleaseSeed.InstalledPlatformVersion;

    private static string InstalledTag => Installed.Split('+')[0];

    /// <summary>A registry with nothing newer than this install, in which its own tag resolves —
    /// so under <c>Continuous</c> a check reaches "no newer release" and rolls nothing.</summary>
    private static string[] NothingNewer => ["1.9.0-ci.0", "2.0.0-ci.0", InstalledTag];

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddUpdatePolicyType().AddGitHubSyncTypes();

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    // ══════════════════════════════════════════════════════════════════════════
    //  The rule, pinned without a mesh
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 🚨 The whole truth table, every trigger × every policy — because the defect and the
    /// regression that would undo #2553 are one enum member apart, and a test that only checked
    /// the <c>None</c>/<c>BuildCompletion</c> cell would pass on a predicate that returned
    /// <c>false</c> for everything.
    /// </summary>
    [Theory]
    // Under None the FLEET-paced triggers decide nothing: RunOnce short-circuits to UpdatesDisabled
    // before it lists a tag, and MayRestartAfter(UpdatesDisabled) is false.
    [InlineData(UpdatePolicyKind.None, SelfUpdateTrigger.BuildCompletion, false)]
    [InlineData(UpdatePolicyKind.None, SelfUpdateTrigger.ModuleSetProposed, false)]
    // …but the three this install paces itself still run, and each one is a #2553 obligation:
    // Startup puts the disabled verdict on the record at all, PolicyChange is how updates get
    // enabled, SafetyNet is what keeps LastCheckedAt moving so a dead checker reads as stale.
    [InlineData(UpdatePolicyKind.None, SelfUpdateTrigger.Startup, true)]
    [InlineData(UpdatePolicyKind.None, SelfUpdateTrigger.PolicyChange, true)]
    [InlineData(UpdatePolicyKind.None, SelfUpdateTrigger.SafetyNet, true)]
    // Under every policy that can act, every trigger is a decision point. This half is what stops
    // the predicate from being "narrow the poller until the log is quiet".
    [InlineData(UpdatePolicyKind.Continuous, SelfUpdateTrigger.BuildCompletion, true)]
    [InlineData(UpdatePolicyKind.Continuous, SelfUpdateTrigger.ModuleSetProposed, true)]
    [InlineData(UpdatePolicyKind.Continuous, SelfUpdateTrigger.Startup, true)]
    [InlineData(UpdatePolicyKind.Continuous, SelfUpdateTrigger.PolicyChange, true)]
    [InlineData(UpdatePolicyKind.Continuous, SelfUpdateTrigger.SafetyNet, true)]
    [InlineData(UpdatePolicyKind.Stable, SelfUpdateTrigger.BuildCompletion, true)]
    [InlineData(UpdatePolicyKind.Stable, SelfUpdateTrigger.ModuleSetProposed, true)]
    [InlineData(UpdatePolicyKind.Stable, SelfUpdateTrigger.Startup, true)]
    [InlineData(UpdatePolicyKind.Stable, SelfUpdateTrigger.PolicyChange, true)]
    [InlineData(UpdatePolicyKind.Stable, SelfUpdateTrigger.SafetyNet, true)]
    public void TheDecisionPointRule(UpdatePolicyKind policy, SelfUpdateTrigger trigger, bool expected) =>
        SelfUpdateHostedService.IsDecisionPoint(trigger, new UpdatePolicyContent { Policy = policy })
            .Should().Be(expected);

    // ══════════════════════════════════════════════════════════════════════════
    //  The same driver, under two policies — a controlled experiment
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 🚨 <b>The acceptance criterion.</b> Policy <c>None</c>; the startup check lands; a build
    /// completion is then pushed through the REAL trigger seam — and no second check happens.
    ///
    /// <para>The bound below would also be satisfied by a service that had simply stopped, which is
    /// why <see cref="UnderContinuous_ABuildCompletion_IsACheck"/> pushes the identical event
    /// through the identical seam and asserts the second check DOES arrive. Neither test means
    /// anything alone.</para>
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task UnderNone_ABuildCompletion_IsNotACheck()
    {
        await Seed(UpdatePolicyKind.None);
        await using var run = await Start();

        await run.FirstCheck;

        run.PushBuildCompletion();

        await run.LaterChecks.Should().NotEmit(TestTimeouts.Quick,
            "under None a build completion can neither roll nor restart (MayRestartAfter is false "
            + "for UpdatesDisabled), so all it could do is rewrite a verdict the node already "
            + "carries — 158 times in 5 h on memex, from both replicas, onto one leaf");
    }

    /// <summary>
    /// 🚨 <b>The positive control</b>, and the guard against "fix" by making the service deaf: the
    /// same seam, the same push, a policy that can act — and the check happens.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task UnderContinuous_ABuildCompletion_IsACheck()
    {
        await Seed(UpdatePolicyKind.Continuous);
        await using var run = await Start();

        await run.FirstCheck;

        run.PushBuildCompletion();

        await run.LaterChecks.FirstAsync().Timeout(Budget).Await(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// 🚨 The #2553 obligation, which is the whole reason the predicate is a filter on the TRIGGER
    /// and not a filter on the check: an install pinned to <c>None</c> still says so, durably, on
    /// the node — "an admin pinned this" and "the updater is broken" must never look alike again.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task UnderNone_TheRecordStillCarriesTheDisabledVerdict()
    {
        await Seed(UpdatePolicyKind.None);
        await using var run = await Start();

        await run.FirstCheck;
        var content = await WaitForContent(c => c.LastCheckVerdict is not null);

        content.LastCheckVerdict.Should().Contain("updates are disabled",
            "the durable half of the report is what an operator reads on the Updates tab");
        content.LastCheckedAt.Should().NotBeNull(
            "a stamp is what distinguishes an install that checked from one that never did");
        content.LastCheckTrigger.Should().Be(nameof(SelfUpdateTrigger.Startup));
    }

    /// <summary>
    /// 🚨 And the stamp keeps MOVING, on this service's own period: the safety net still runs under
    /// <c>None</c>, so a checker that has died still reads as a stale <c>LastCheckedAt</c> rather
    /// than as one frozen by design. This is the cell that stops the fix from becoming a freeze.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task UnderNone_TheSafetyNet_StillChecks()
    {
        await Seed(UpdatePolicyKind.None);
        await using var run = await Start();

        await run.FirstCheck;

        run.PushSafetyNet();

        await run.LaterChecks.FirstAsync().Timeout(Budget).Await(TestContext.Current.CancellationToken);
        var content = await WaitForContent(c => c.LastCheckTrigger == nameof(SelfUpdateTrigger.SafetyNet));
        content.LastCheckVerdict.Should().Contain("updates are disabled");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Harness
    // ══════════════════════════════════════════════════════════════════════════

    private sealed class FakeAcrTagLister(IReadOnlyList<string> tags) : IAcrTagLister
    {
        public Task<IReadOnlyList<string>> ListTagsAsync(string repository, CancellationToken ct) =>
            Task.FromResult(tags);
    }

    /// <summary>Detect-and-notify: this suite is about which triggers reach a check, never about
    /// what a check then does to a cluster.</summary>
    private sealed class InertUpdater : IDeploymentUpdater
    {
        public bool CanPatch => false;

        public Task<DateTimeOffset?> LastRolledAtAsync(CancellationToken ct) =>
            Task.FromResult<DateTimeOffset?>(null);

        public Task PatchToVersionAsync(string versionTag, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class AlwaysAvailable(IMessageHub hub, IConfiguration configuration)
        : ReleaseAvailabilityService(hub, configuration)
    {
        public override IObservable<UpdatabilityVerdict> IsUpdatable(string? targetVersion) =>
            Observable.Return(UpdatabilityVerdict.NotEnforced("this test host consumes no CI bakes"));
    }

    /// <summary>The poller with its two fleet-paced trigger seams driven by hand, so a build
    /// completion is an EVENT the test pushes rather than a timer it waits out.</summary>
    private sealed class DrivenSelfUpdateService(
        IMessageHub hub,
        IAcrTagLister acr,
        SelfUpdateOptions options,
        ILogger<SelfUpdateHostedService>? logger,
        ReleaseAvailabilityService gate,
        IObservable<SelfUpdateTrigger> builds,
        IObservable<SelfUpdateTrigger> safetyNet)
        : SelfUpdateHostedService(hub, acr, new InertUpdater(), options, logger)
    {
        public IObservable<Unit> Evaluations => ChecksReported;

        protected override ReleaseAvailabilityService? ResolveAvailabilityGate() => gate;

        protected override ComboVerificationGate? ResolveComboGate() => null;

        protected override ModuleLandingService? ResolveLandingService() => null;

        protected override IObservable<SelfUpdateTrigger> BuildCompletionTicks() => builds;

        protected override IObservable<SelfUpdateTrigger> SafetyNetTicks() => safetyNet;
    }

    /// <summary>One running service plus the two things every test here needs: a signal that the
    /// startup check has been REPORTED, and a stream of every check after it.
    ///
    /// <para>🚨 The checks are <c>Replay()</c>ed and connected BEFORE <c>StartAsync</c>. A live
    /// <c>Publish()</c> would make every assertion here a race against the startup check landing in
    /// the gap between subscribe and start — and the shape that race fails in is a HANG, which is
    /// how a suite about a rate would end up bounding one.</para></summary>
    private sealed class Run(
        SelfUpdateHostedService service,
        Subject<SelfUpdateTrigger> builds,
        Subject<SelfUpdateTrigger> safetyNet,
        IObservable<Unit> checks,
        IDisposable connection) : IAsyncDisposable
    {
        /// <summary>The startup check, reported. Replayed, so awaiting it after the fact works.</summary>
        public Task FirstCheck => checks.FirstAsync().Timeout(Budget)
            .Await(TestContext.Current.CancellationToken);

        /// <summary>Every check reported after the startup one.</summary>
        public IObservable<Unit> LaterChecks => checks.Skip(1);

        public void PushBuildCompletion() => builds.OnNext(SelfUpdateTrigger.BuildCompletion);

        public void PushSafetyNet() => safetyNet.OnNext(SelfUpdateTrigger.SafetyNet);

        public async ValueTask DisposeAsync()
        {
            await service.StopAsync(CancellationToken.None);
            connection.Dispose();
            builds.Dispose();
            safetyNet.Dispose();
        }
    }

    private async Task<Run> Start()
    {
        var builds = new Subject<SelfUpdateTrigger>();
        var safetyNet = new Subject<SelfUpdateTrigger>();
        var service = new DrivenSelfUpdateService(
            Mesh, new FakeAcrTagLister(NothingNewer),
            new SelfUpdateOptions
            {
                RetryInterval = TimeSpan.FromMilliseconds(500),
                EventCoalesceWindow = TimeSpan.FromMilliseconds(50),
                DefaultPolicy = UpdatePolicyKind.Continuous,
                DefaultPattern = "*-ci*",
            },
            Mesh.ServiceProvider.GetService<ILogger<SelfUpdateHostedService>>(),
            new AlwaysAvailable(Mesh, new ConfigurationBuilder().Build()),
            builds, safetyNet);
        var checks = service.Evaluations.Replay();
        var connection = checks.Connect();
        await service.StartAsync(CancellationToken.None);
        return new Run(service, builds, safetyNet, checks, connection);
    }

    private Task Seed(UpdatePolicyKind policy)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var node = new MeshNode(UpdatePolicyNodeType.NodeId, UpdatePolicyNodeType.AdminPartition)
        {
            NodeType = UpdatePolicyNodeType.NodeType,
            Name = "Update Policy",
            State = MeshNodeState.Active,
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
