#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>A roll the install cannot migrate is REFUSED, and a roll it takes without a migration SAYS
/// SO on the record (#4764).</b>
///
/// <para>The migration is a run-once <c>Job</c>: only <c>helm upgrade</c> ever rendered one, and the
/// self-updater patches container images with a strategic merge. So the roll and the schema move are
/// two different acts, and a roll that makes the first without the second is unrecoverable
/// in-process — <c>DbVersionGate</c> correctly refuses the new pods (<c>db_version=N &lt; expected
/// N+1</c>, <c>LogCritical</c>, exit 1), Kubernetes restarts them for ever, and the previous
/// ReplicaSet keeps answering HTTP 200 the whole time. Measured on memex-cloud 2026-09-19:
/// <c>CrashLoopBackOff</c> with restarts=2 by 05:06Z, 3319 ms to the exit, desired 4 / ready 4 all on
/// the OLD set, updated 1, unavailable 1 — nothing converging, nothing rolling back, and the record
/// still saying the roll was made.</para>
///
/// <para><b>What is under test is the DECISION, in both directions</b> — the rule being that only
/// <c>Completed</c> proves the schema moved:</para>
/// <list type="bullet">
/// <item><c>Forbidden</c> (the cluster refused the Job, 403) ⇒ <b>REFUSED</b>, nothing patched, and
/// the verdict names the one command that fixes it. It used to patch "loudly", which is the branch
/// #4764 measured.</item>
/// <item><c>Failed</c> ⇒ <b>REFUSED</b> too, and with a DIFFERENT sentence: the migration ran and
/// broke, so the operator reads a Job log rather than running helm.</item>
/// <item><c>NotSupported</c> ⇒ still <b>ROLLS</b> — an install whose updater can never migrate would
/// otherwise freeze for ever and silently (#2553) — but the roll is recorded <c>UNMIGRATED</c>,
/// naming the module to update.</item>
/// <item><c>Completed</c> ⇒ rolls, and carries NO qualifier: a schema that provably moved must not
/// read like one that did not, or the qualifier stops meaning anything.</item>
/// </list>
///
/// <para>Driven against a real monolith mesh, with only the two documented IO seams faked (the
/// registry list and the Kubernetes updater). The hub, the workspace, the policy node, the live
/// stream and every <c>stream.Update</c> are real, and the refusal is asserted POSITIVELY — an
/// unpatched updater PLUS the sentence on <c>Admin/UpdatePolicy</c> — never as "no patch happened",
/// which would also pass against a poller that simply died.</para>
///
/// <para><b>Fails on unfixed code:</b> <see cref="A403OnTheMigrationJob_RefusesTheRoll_AndNamesTheHelmRemedy"/>
/// reds because the <c>Forbidden</c> branch patches and records "applied update", and
/// <see cref="AnUpdaterThatCannotMigrate_StillRolls_AndTheRecordSaysItRolledBlind"/> reds because
/// that roll is recorded as a plain <c>Applied</c> with nothing to distinguish it from a migrated
/// one.</para>
/// </summary>
public class SelfUpdateSchemaBumpRefusalTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string CandidateTag = "9999.0.0-ci.1";

    /// <summary>🚨 <see cref="TestTimeouts.Convergence"/>, never a literal: a hand-written 30 s is
    /// both a guess about machine speed AND the framework's own write bound, so a test that waits it
    /// gives up one second before the mesh can explain itself (#2819).</summary>
    private static TimeSpan Budget => TestTimeouts.Convergence;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        // AddGitHubSyncTypes registers the BuildCompletion satellite the self-update watch reacts
        // to — types only, the same production registration rather than a duplicate declaration.
        => base.ConfigureMesh(builder).AddUpdatePolicyType().AddGitHubSyncTypes();

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    // ══════════════════════════════════════════════════════════════════════════
    //  The refusals
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 🚨 <b>The acceptance criterion for #4764.</b> The cluster answered 403 to the migration Job, so
    /// NOTHING was established about the schema — and the one move that cannot be undone from inside
    /// the process is to patch the image anyway. The refusal must be visible where an operator looks,
    /// and it must name the remedy, because a 403 means this install's chart predates the
    /// <c>batch/jobs</c> grant and the <c>helm upgrade</c> that adds it runs the migration itself.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task A403OnTheMigrationJob_RefusesTheRoll_AndNamesTheHelmRemedy()
    {
        await Seed(TestContext.Current.CancellationToken);
        var updater = new RecordingUpdater(MigrationRunOutcome.Forbidden);

        var content = await RunOneCheck(updater);

        updater.Migrations.Should().Contain(CandidateTag,
            "the schema moves FIRST — a poller that patches without even asking is the 2026-09-03 "
            + "wedge, and this test would otherwise pass against one");
        updater.Tags.Should().BeEmpty(
            "nothing established that the schema is where this build needs it, and a build expecting "
            + "a newer db_version than the database has crash-loops on DbVersionGate behind pods that "
            + "keep answering 200 — the state measured on memex-cloud 2026-09-19");
        content.LastCheckVerdict.Should().Contain("REFUSED",
            "a refusal that leaves no trace on the policy node is the silent freeze this whole area "
            + "must never become — and a log line depends on a level a deployment may never have set");
        content.LastCheckVerdict.Should().Contain("batch/jobs",
            "an unnamed refusal is unactionable: the operator has to know WHICH permission is missing");
        content.LastCheckVerdict.Should().Contain("helm upgrade",
            "the remedy is the one command that both grants the permission and runs the migration");
        content.LastCheckVerdict.Should().NotContain("applied update",
            "the record said the roll was made while the crash-loop lived only on the pod — that is "
            + "#4764's second ask, and this is the sentence it was about");
    }

    /// <summary>
    /// A migration that RAN and broke refuses too — pre-existing behaviour, pinned here beside the
    /// new refusal because the two sentences must stay DIFFERENT. "Read the Job log" and "run a helm
    /// upgrade" are different next moves, and one message for both would send an operator to the
    /// wrong one.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task AMigrationThatRanAndFailed_RefusesTheRoll_AndPointsAtTheJobLog()
    {
        await Seed(TestContext.Current.CancellationToken);
        var updater = new RecordingUpdater(MigrationRunOutcome.Failed);

        var content = await RunOneCheck(updater);

        updater.Tags.Should().BeEmpty("the schema demonstrably did not move, so the image must not");
        content.LastCheckVerdict.Should().Contain("REFUSED");
        content.LastCheckVerdict.Should().Contain("memex-migration-su-",
            "the operator's next move is to read THAT Job's log, so the verdict has to name it");
        content.LastCheckVerdict.Should().NotContain("batch/jobs",
            "this migration was created and ran — pointing at a missing permission would be a lie "
            + "that costs an operator the time to disprove");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  The two rolls — one blind and recorded as such, one proven and unqualified
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 🚨 <b>The #2553 side of the rule.</b> An updater that has no migration mechanism at all — an
    /// installed <c>MeshWeaver.SelfUpdate.Aks</c> generation predating the seam, which is what the
    /// interface default answers — must NOT be frozen: refusing every roll for ever, silently, is the
    /// worse failure shape. What must change is that the roll stops being indistinguishable from one
    /// whose schema moved.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task AnUpdaterThatCannotMigrate_StillRolls_AndTheRecordSaysItRolledBlind()
    {
        await Seed(TestContext.Current.CancellationToken);
        var updater = new RecordingUpdater(MigrationRunOutcome.NotSupported);

        var content = await RunOneCheck(updater);

        updater.Tags.Should().Contain(CandidateTag,
            "refusing on 'this install can NEVER migrate' would freeze it until an operator acts, "
            + "silently — the worse failure shape (#2553)");
        content.LastCheckVerdict.Should().Contain("UNMIGRATED",
            "an unmigrated roll that leaves no durable trace is indistinguishable from a migrated "
            + "one, which is exactly what #4764 measured on the policy node");
        content.LastCheckVerdict.Should().Contain("MeshWeaver.SelfUpdate.Aks",
            "the state is only actionable if the record names what to update");
        content.LastCheckVerdict.Should().Contain("DbVersionGate",
            "and names what will happen if this build does expect a newer schema");
    }

    /// <summary>
    /// The other side of the change: a migration that COMPLETED rolls with no qualifier at all. A
    /// proven schema move reading like an unproven one would make the qualifier meaningless, which is
    /// the same failure as not recording it.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task AMigrationThatCompleted_RollsUnqualified()
    {
        await Seed(TestContext.Current.CancellationToken);
        var updater = new RecordingUpdater(MigrationRunOutcome.Completed);

        var content = await RunOneCheck(updater);

        updater.Migrations.Should().Contain(CandidateTag);
        updater.Tags.Should().Contain(CandidateTag);
        content.LastCheckVerdict.Should().Contain("applied update");
        content.LastCheckVerdict.Should().NotContain("UNMIGRATED",
            "the migration Job for this tag succeeded — the schema IS where the build needs it, and "
            + "qualifying that would train operators to ignore the qualifier");
        content.LastCheckVerdict.Should().NotContain("REFUSED");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  One decision, two routes
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 🚨 <b>The poller's behaviour IS <see cref="SelfUpdateVerdict.MayPatchAfter"/>, for every
    /// outcome.</b>
    ///
    /// <para>There are two routes that patch — this poller and the Updates tab's manual Apply — and
    /// the second one skipped the migration entirely until #4764, which is what a per-route switch
    /// statement costs. The tab now reads the predicate; this drives the poller end-to-end over all
    /// five outcomes and asserts it agrees, so an outcome ADDED to the enum cannot be handled one way
    /// in one route and another in the other without a red test.</para>
    ///
    /// <para>Not a restatement of the predicate: the predicate is the tab's decision, and what is
    /// measured here is whether a REAL check, against a real mesh, patched — read off the fake
    /// updater's recorded tags rather than off any verdict text.</para>
    /// </summary>
    [Theory(Timeout = 240_000)]
    [InlineData(MigrationRunOutcome.Completed)]
    [InlineData(MigrationRunOutcome.Failed)]
    [InlineData(MigrationRunOutcome.TimedOut)]
    [InlineData(MigrationRunOutcome.Forbidden)]
    [InlineData(MigrationRunOutcome.NotSupported)]
    public async Task ThePollerPatchesExactlyWhenTheSharedDecisionSaysItMay(MigrationRunOutcome outcome)
    {
        await Seed(TestContext.Current.CancellationToken);
        var updater = new RecordingUpdater(outcome);

        await RunOneCheck(updater);

        updater.Migrations.Should().Contain(CandidateTag,
            "every roll asks about the schema first, whatever the answer turns out to be");
        if (SelfUpdateVerdict.MayPatchAfter(outcome))
            updater.Tags.Should().Contain(CandidateTag,
                "MayPatchAfter says this outcome permits the patch, and the manual Apply route will "
                + "patch on it — the poller must not be stricter than the predicate the other route "
                + "reads, or the two disagree about the same release");
        else
            updater.Tags.Should().BeEmpty(
                "MayPatchAfter refuses this outcome, and the manual Apply route will refuse on it — "
                + "the poller must not be more permissive than the predicate the other route reads, "
                + "which is precisely how the button ended up making the roll the poller had stopped "
                + "making (#4764)");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Harness — the ComboGateRollTest shapes, with the migration seam driven
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Starts the poller, waits for ONE check to have been evaluated AND reported, then reads the
    /// policy content that check produced.
    ///
    /// <para>🚨 Waits for the service to have EVALUATED, never for a state to appear inside a bound:
    /// this service is event-driven, so "the state is not there yet" and "the first check has not run
    /// yet" are indistinguishable from outside, and on a loaded shard the second is what actually
    /// happens — reported as if it were a wrong verdict.</para>
    /// </summary>
    private async Task<UpdatePolicyContent> RunOneCheck(RecordingUpdater updater)
    {
        var ct = TestContext.Current.CancellationToken;
        var service = new GatedSelfUpdateService(
            Mesh, new FakeAcrTagLister(), updater, FastPoll(),
            Mesh.ServiceProvider.GetService<ILogger<SelfUpdateHostedService>>(),
            new AlwaysAvailable(Mesh, new ConfigurationBuilder().Build()),
            new ComboVerificationGate(Mesh));

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

    private static SelfUpdateOptions FastPoll() => new()
    {
        RetryInterval = TimeSpan.FromMilliseconds(500),
        EventCoalesceWindow = TimeSpan.FromMilliseconds(50),
        DefaultPolicy = UpdatePolicyKind.Continuous,
        DefaultPattern = "*-ci*",
    };

    /// <summary>Fake registry (the documented IO seam): one build newer than anything installed.</summary>
    private sealed class FakeAcrTagLister : IAcrTagLister
    {
        public Task<IReadOnlyList<string>> ListTagsAsync(string repository, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([CandidateTag]);
    }

    /// <summary>
    /// Fake Kubernetes updater (the documented IO seam) — records every tag it was asked to MIGRATE
    /// and every tag it was asked to PATCH, separately, because the whole question here is whether
    /// the second happens when the first did not succeed.
    /// </summary>
    private sealed class RecordingUpdater(MigrationRunOutcome outcome) : IDeploymentUpdater
    {
        private ImmutableList<string> tags = ImmutableList<string>.Empty;
        private ImmutableList<string> migrations = ImmutableList<string>.Empty;

        public ImmutableList<string> Tags => tags;

        public ImmutableList<string> Migrations => migrations;

        public bool CanPatch => true;

        public Task<DateTimeOffset?> LastRolledAtAsync(CancellationToken ct) =>
            Task.FromResult<DateTimeOffset?>(null);

        public Task PatchToVersionAsync(string versionTag, CancellationToken ct)
        {
            ImmutableInterlocked.Update(ref tags, current => current.Add(versionTag));
            return Task.CompletedTask;
        }

        public Task<MigrationRunOutcome> RunMigrationAsync(string versionTag, CancellationToken ct)
        {
            ImmutableInterlocked.Update(ref migrations, current => current.Add(versionTag));
            return Task.FromResult(outcome);
        }
    }

    /// <summary>
    /// The availability gate, pinned to "nothing to enforce here". The gates are INDEPENDENT and this
    /// suite is about the migration step — leaving another gate free to decide would make every
    /// assertion here ambiguous about which one produced the outcome.
    /// </summary>
    private sealed class AlwaysAvailable(IMessageHub hub, IConfiguration configuration)
        : ReleaseAvailabilityService(hub, configuration)
    {
        public override IObservable<UpdatabilityVerdict> IsUpdatable(string? targetVersion) =>
            Observable.Return(UpdatabilityVerdict.NotEnforced(
                "this test host consumes no CI bakes"));
    }

    /// <summary>The poller with both gates supplied directly. Production resolves them from the
    /// mesh's service provider; injecting keeps the test from rebuilding the mesh to register two
    /// singletons, without changing which code path decides. The combo gate is the portal-pod shape,
    /// whose "no verdict recorded" answer neither clears nor refuses — so the roll reaches the
    /// migration step, which is the step under test.</summary>
    private sealed class GatedSelfUpdateService(
        IMessageHub hub,
        IAcrTagLister acr,
        IDeploymentUpdater updater,
        SelfUpdateOptions options,
        ILogger<SelfUpdateHostedService>? logger,
        ReleaseAvailabilityService? gate,
        ComboVerificationGate? combo)
        : SelfUpdateHostedService(hub, acr, updater, options, logger)
    {
        /// <summary>Surfaces the base class's per-check completion. The base member is
        /// <c>protected internal</c> and this assembly is not in its friend list, so a derived-class
        /// forward is the local way to reach it.</summary>
        public IObservable<Unit> Evaluations => ChecksReported;

        protected override ReleaseAvailabilityService? ResolveAvailabilityGate() => gate;

        protected override ComboVerificationGate? ResolveComboGate() => combo;
    }

    // ── mesh helpers (the ComboGateRollTest shapes) ──

    private Task Seed(CancellationToken cancellationToken)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        // The candidate is a ci build, eligible only under a pattern that admits it.
        var node = new MeshNode(UpdatePolicyNodeType.NodeId, UpdatePolicyNodeType.AdminPartition)
        {
            NodeType = UpdatePolicyNodeType.NodeType,
            Name = "Update Policy",
            State = MeshNodeState.Active,
            Content = new UpdatePolicyContent
            {
                Policy = UpdatePolicyKind.Continuous,
                Pattern = "*-ci*",
            },
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
            .Await(cancellationToken);
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
