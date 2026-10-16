#pragma warning disable CS1591

using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The watchdog's rules, pure (<c>Doc/Architecture/InstanceReboot</c> → "Self-trigger"): what counts as
/// a wedge, how "continuously in Error" is folded, and the rate limit — each with its negative control.
/// </summary>
public class InstanceRebootWatchdogRulesTest
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly InstanceRebootOptions Options = new();

    private static WedgeSignal Fault(int minutesAgo, string kind = WedgeSignalKinds.ThreadStart) =>
        new(kind, "Hosting/TriageIntake", "MissingMethodException: Method not found: 'Void ThreadPreparation.set_Group(String)'", Now.AddMinutes(-minutesAgo));

    [Fact]
    public void OnlyLoadAndBindingFaults_AreKept()
    {
        RebootWatchdogRules.LoadOrBindingFault(new MissingMethodException("ThreadPreparation", "set_Group")).Should().StartWith("MissingMethodException");
        RebootWatchdogRules.LoadOrBindingFault(new TypeLoadException("x")).Should().StartWith("TypeLoadException");
        RebootWatchdogRules.LoadOrBindingFault(new TargetInvocationException(new MissingFieldException("x"))).Should().StartWith("MissingFieldException");
        RebootWatchdogRules.LoadOrBindingFault(new AggregateException(new InvalidOperationException("a"), new TypeLoadException("b"))).Should().StartWith("TypeLoadException");
        RebootWatchdogRules.LoadOrBindingFault(new FileNotFoundException("gone", "MeshWeaver.AI, Version=1.21.0.0, Culture=neutral")).Should().StartWith("FileNotFoundException");
        // Negative controls: an ordinary failure, and a missing DATA file, are not wedges.
        RebootWatchdogRules.LoadOrBindingFault(new InvalidOperationException("the model refused")).Should().BeNull();
        RebootWatchdogRules.LoadOrBindingFault(new FileNotFoundException("gone", "/data/notes.md")).Should().BeNull();
        RebootWatchdogRules.LoadOrBindingFault(new TimeoutException()).Should().BeNull();
    }

    [Fact]
    public void ThreadStartLeg_FiresAtTheThreshold_InsideTheWindow_AndNotBelowOrOutside()
    {
        var empty = ImmutableDictionary<string, DateTimeOffset>.Empty;
        RebootWatchdogRules.Evaluate([Fault(1), Fault(2), Fault(3)], empty, Now, Options).Should().NotBeNull();
        // Below the threshold.
        RebootWatchdogRules.Evaluate([Fault(1), Fault(2)], empty, Now, Options).Should().BeNull();
        // Outside the window.
        RebootWatchdogRules.Evaluate([Fault(1), Fault(2), Fault(30)], empty, Now, Options).Should().BeNull();
        // Another kind of signal does not count toward the thread-start leg.
        RebootWatchdogRules.Evaluate([Fault(1, "Other"), Fault(2, "Other"), Fault(3, "Other")], empty, Now, Options).Should().BeNull();
    }

    [Fact]
    public void CompileLeg_NeedsContinuousErrorForTheWholeDuration_AndAnUnreadablePassResets()
    {
        var since = ImmutableDictionary<string, DateTimeOffset>.Empty;
        since = RebootWatchdogRules.FoldCompileErrors(since, ["Hosting/TriageItem"], Now.AddMinutes(-25));
        since = RebootWatchdogRules.FoldCompileErrors(since, ["Hosting/TriageItem"], Now);
        RebootWatchdogRules.Evaluate([], since, Now, Options)!.Evidence.Should().Contain("Hosting/TriageItem");

        // A pass that does not see it removes it — "continuously" means every pass.
        var gap = RebootWatchdogRules.FoldCompileErrors(since, [], Now);
        RebootWatchdogRules.Evaluate([], RebootWatchdogRules.FoldCompileErrors(gap, ["Hosting/TriageItem"], Now), Now, Options).Should().BeNull();

        // A pass that could not read resets: no evidence is not evidence of a wedge.
        RebootWatchdogRules.FoldCompileErrors(since, null, Now).Should().BeEmpty();

        // Not long enough yet.
        var young = RebootWatchdogRules.FoldCompileErrors(ImmutableDictionary<string, DateTimeOffset>.Empty, ["Hosting/TriageItem"], Now.AddMinutes(-5));
        RebootWatchdogRules.Evaluate([], young, Now, Options).Should().BeNull();
    }

    [Fact]
    public void TheRateLimit_RefusesAnOpenReboot_ARecentSelfReboot_AndTheSameEvidenceAgain()
    {
        var verdict = new WedgeVerdict("e", "fp1");
        RebootWatchdogRules.Admit(verdict, [], Now, Options).Fire.Should().BeTrue();

        RebootWatchdogRules.Admit(verdict, [new("Admin/_Reboot/a", InstanceRebootTrigger.Person, Now.AddMinutes(-2), null, Terminal: false)], Now, Options)
            .Why.Should().Contain("already in progress");
        RebootWatchdogRules.Admit(verdict, [new("Admin/_Reboot/a", InstanceRebootTrigger.Watchdog, Now.AddHours(-1), "other", Terminal: true)], Now, Options)
            .Why.Should().Contain("rate-limited");
        RebootWatchdogRules.Admit(verdict, [new("Admin/_Reboot/a", InstanceRebootTrigger.Watchdog, Now.AddHours(-8), "fp1", Terminal: true)], Now, Options)
            .Why.Should().Contain("same evidence survived");

        // Controls: a PERSON's finished reboot an hour ago does not rate-limit the watchdog; a self-reboot
        // with OTHER evidence beyond the interval does not either.
        RebootWatchdogRules.Admit(verdict, [new("Admin/_Reboot/a", InstanceRebootTrigger.Person, Now.AddHours(-1), null, Terminal: true)], Now, Options).Fire.Should().BeTrue();
        RebootWatchdogRules.Admit(verdict, [new("Admin/_Reboot/a", InstanceRebootTrigger.Watchdog, Now.AddHours(-8), "fp2", Terminal: true)], Now, Options).Fire.Should().BeTrue();
    }

    /// <summary>
    /// 🚨 MeshWeaver#6625 (+ review): a stalled request releases the self-reboot only once its OWNER ended
    /// it. While it is still open — stalled-looking or not — it holds the self-reboot back, because it could
    /// still be re-armed and roll beside a replacement.
    /// </summary>
    [Fact]
    public void AStalledReboot_ReleasesTheSelfRebootOnlyOnceItsOwnerEndedIt()
    {
        var verdict = new WedgeVerdict("e", "fp1");
        var ended = new RebootRecord("Admin/_Reboot/a", InstanceRebootTrigger.Person, Now.AddHours(-21), null, Terminal: true)
        {
            Stalled = "status Faulted, no progress since …",
        };
        RebootWatchdogRules.Admit(verdict, [ended], Now, Options).Fire.Should().BeTrue();
        RebootWatchdogRules.Admit(verdict, [ended with { Terminal = false }], Now, Options).Why.Should().Contain("already in progress");
    }

    private static readonly TimeSpan RetryUnit = TimeSpan.FromMinutes(30);

    [Fact]
    public void ARequest_StallsWhenItRecordsNoProgressWithinItsDeclaredWindow_ByStatus()
    {
        var window = TimeSpan.FromHours(3);
        var restart = Now.AddHours(-4);
        var awaiting = new InstanceRebootRequest
        {
            Reason = "x",
            RequestedAt = Now.AddHours(-5),
            Status = InstanceRebootStatus.AwaitingRestart,
            RestartRequestedAt = restart,
            ProgressAt = restart,
            ExecutionWindow = window,
        };
        InstanceReboot.Stalled(awaiting, Now, Options.ExecutionWindow, RetryUnit)
            .Should().Contain("AwaitingRestart").And.Contain("no process booted after it has reported");

        // Controls: a replica that reported an hour ago is progress; so is a wider declared window; and a
        // terminal request is never "stalled", however old.
        InstanceReboot.Stalled(awaiting with
        {
            Replicas = ImmutableDictionary<string, InstanceRebootReplica>.Empty.Add("p",
                new InstanceRebootReplica { Process = "p", StartedAt = Now.AddHours(-2), ReportedAt = Now.AddHours(-1) }),
        }, Now, Options.ExecutionWindow, RetryUnit).Should().BeNull();
        InstanceReboot.Stalled(awaiting with { ExecutionWindow = TimeSpan.FromHours(5) }, Now, Options.ExecutionWindow, RetryUnit).Should().BeNull();
        InstanceReboot.Stalled(awaiting with { Status = InstanceRebootStatus.Failed }, Now, Options.ExecutionWindow, RetryUnit).Should().BeNull();

        // A request filed before the window was declared is judged by the reader's default.
        InstanceReboot.Stalled(awaiting with { ExecutionWindow = null }, Now, TimeSpan.FromHours(5), RetryUnit).Should().BeNull();
        InstanceReboot.Stalled(awaiting with { ExecutionWindow = null }, Now, window, RetryUnit).Should().NotBeNull();

        // Preparing: the step it stopped at is named.
        var preparing = new InstanceRebootRequest
        {
            Reason = "x",
            RequestedAt = Now.AddHours(-21),
            Status = InstanceRebootStatus.Preparing,
            Steps = InstanceReboot.WithStep(InstanceReboot.FreshSteps(), InstanceRebootSteps.Sync, InstanceRebootStepOutcome.Running, "importing",
                Now.AddHours(-21)),
        };
        InstanceReboot.Stalled(preparing, Now, Options.ExecutionWindow, RetryUnit).Should().Contain("its last step is Sync (Running)");

        // Faulted is judged from when its re-arm was DUE: a long backoff still ahead is not a stall.
        var faulted = new InstanceRebootRequest
        {
            Reason = "x",
            RequestedAt = Now.AddHours(-20),
            Status = InstanceRebootStatus.Faulted,
            FaultedAt = Now.AddHours(-10),
            Attempt = 5, // 30 min × 32 = 16 h of backoff: due in 6 h
        };
        InstanceReboot.Stalled(faulted, Now, Options.ExecutionWindow, RetryUnit).Should().BeNull();
        InstanceReboot.Stalled(faulted with { Attempt = 0 }, Now, Options.ExecutionWindow, RetryUnit)
            .Should().Contain("nothing re-armed it when its retry was due");
    }

    /// <summary>
    /// #6625 review: a long Sync batch is progress all the way through — each finished source stamps
    /// <see cref="InstanceRebootRequest.ProgressAt"/>, so a step that has been Running for longer than the
    /// window is NOT stalled while its sources keep finishing. Control: the same step with no per-source
    /// stamp is.
    /// </summary>
    [Fact]
    public void ASyncBatchLongerThanTheWindow_IsNotStalled_WhileItsSourcesKeepFinishing()
    {
        var batch = new InstanceRebootRequest
        {
            Reason = "x",
            RequestedAt = Now.AddHours(-4),
            Status = InstanceRebootStatus.Preparing,
            ExecutionWindow = TimeSpan.FromHours(3),
            Steps = InstanceReboot.WithStep(InstanceReboot.FreshSteps(), InstanceRebootSteps.Sync, InstanceRebootStepOutcome.Running,
                "importing", Now.AddHours(-4)),
            ProgressAt = Now.AddMinutes(-9), // the 21st 9-minute import just finished
        };
        InstanceReboot.Stalled(batch, Now, Options.ExecutionWindow, RetryUnit).Should().BeNull();
        InstanceReboot.Stalled(batch with { ProgressAt = Now.AddHours(-4) }, Now, Options.ExecutionWindow, RetryUnit)
            .Should().Contain("its last step is Sync (Running)");
    }

    /// <summary>#6625 review: an ENDED request is named as stalled from its persisted marker, whoever ended it.</summary>
    [Fact]
    public void AnEndedRequest_IsNamedAsStalled_FromItsPersistedMarker()
    {
        var ended = InstanceReboot.EndIfStalled(new InstanceRebootRequest
        {
            Reason = "x",
            RequestedAt = Now.AddHours(-5),
            Status = InstanceRebootStatus.AwaitingRestart,
            RestartRequestedAt = Now.AddHours(-4),
            ProgressAt = Now.AddHours(-4),
            ExecutionWindow = TimeSpan.FromHours(3),
        }, Now, Options.ExecutionWindow, RetryUnit);
        InstanceReboot.EndedAsStalled(ended).Should().Contain("no process booted after it has reported");
        // Controls: Failed for another reason, Done, and an open request are not "ended as stalled".
        InstanceReboot.EndedAsStalled(ended with { Failure = "Verify: smoke red" }).Should().BeNull();
        InstanceReboot.EndedAsStalled(ended with { Status = InstanceRebootStatus.Done }).Should().BeNull();
        InstanceReboot.EndedAsStalled(ended with { Status = InstanceRebootStatus.AwaitingRestart }).Should().BeNull();
    }

    /// <summary>
    /// #6625 review: the end is decided by the OWNER, against its current state. A replica report that
    /// landed after a reader judged the request stalled keeps it open; without it, the owner ends it.
    /// </summary>
    [Fact]
    public void TheOwnerEndsOnlyWhatIsStillStalled_AndAlwaysClearsTheQuestion()
    {
        var asked = new InstanceRebootRequest
        {
            Reason = "x",
            RequestedAt = Now.AddHours(-5),
            Status = InstanceRebootStatus.AwaitingRestart,
            RestartRequestedAt = Now.AddHours(-4),
            ProgressAt = Now.AddHours(-4),
            ExecutionWindow = TimeSpan.FromHours(3),
            RequestedEndIfStalledAt = Now.AddSeconds(-1),
        };
        var ended = InstanceReboot.EndIfStalled(asked, Now, Options.ExecutionWindow, RetryUnit);
        ended.Status.Should().Be(InstanceRebootStatus.Failed);
        ended.Failure.Should().StartWith("stalled:");
        ended.RequestedEndIfStalledAt.Should().BeNull();

        var reported = asked with
        {
            Replicas = ImmutableDictionary<string, InstanceRebootReplica>.Empty.Add("p",
                new InstanceRebootReplica { Process = "p", StartedAt = Now.AddMinutes(-2), ReportedAt = Now.AddSeconds(-1) }),
        };
        var kept = InstanceReboot.EndIfStalled(reported, Now, Options.ExecutionWindow, RetryUnit);
        kept.Status.Should().Be(InstanceRebootStatus.AwaitingRestart);
        kept.RequestedEndIfStalledAt.Should().BeNull();
        kept.ProgressAt.Should().Be(asked.ProgressAt, "answering the question is not progress");
    }
}

/// <summary>
/// 🚨 The self-trigger on a real mesh: the watchdog fires on the wedge predicate (thread starts failing
/// with a load/binding fault), files ONE reboot as the watchdog, and refuses — alarmed, rate-limited —
/// to fire again; on a healthy instance, and on ordinary (non-binding) failures, it files nothing.
/// </summary>
public class InstanceRebootWatchdogTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            .AddGitHubSyncTypes()
            .AddPluginCatalog()
            .ConfigureServices(services =>
            {
                services.AddGitHubSyncServices();
                // Enabled, but never armed on a timer here: each pass is driven by the test.
                return services.AddSingleton(new InstanceRebootOptions
                {
                    WatchdogEnabled = true,
                    WatchdogInterval = TimeSpan.Zero,
                    SingletonPasses = [new RebootSingletonPass("PR babysitter", BabysitterPath, "lastRunAt"),
                        new RebootSingletonPass("PR review sweep", "Ops/Triage/Status", "lastPrSweepAt")],
                    SingletonResumeBudget = TestTimeouts.Quick,
                });
            });

    private RebootWatchdog Watchdog => Mesh.ServiceProvider.GetRequiredService<RebootWatchdog>();
    private WedgeSignals Signals => Mesh.ServiceProvider.GetRequiredService<WedgeSignals>();
    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private Task<ImmutableList<InstanceRebootRequest>> Reboots() =>
        Access.RunAsSystem(() => Mesh.ServiceProvider.GetRequiredService<IMeshService>().Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"namespace:{InstanceRebootRequest.Namespace} scope:children nodeType:{InstanceRebootRequest.NodeType}")).Take(1))
            .Select(change => change.Items.Select(n => n.ContentAs<InstanceRebootRequest>(Mesh.JsonSerializerOptions)!).ToImmutableList())
            .Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);

    /// <summary>
    /// The control lane's Reboot: its plan is FIXED (the control side binds the digest without a dry run),
    /// its target is the request namespace, and executing it files ONE person-triggered reboot carrying
    /// the control-side requester.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task TheLaneReboot_HasAFixedPlan_AndFilesOneRequestForTheRequester()
    {
        var ct = TestContext.Current.CancellationToken;
        var operation = new MeshWeaver.Graph.ControlLane.RebootOperation();
        var lane = new MeshWeaver.Graph.ControlLane.ControlLaneRequest
        {
            RequestId = "r-0123456789abcdef",
            Deployment = "memex",
            Operation = MeshWeaver.Graph.ControlLane.ControlLaneOperation.Reboot,
            Target = MeshWeaver.Graph.ControlLane.RebootOperation.Target,
            Reason = "thread starts throw",
            RequestedBy = "alice",
            ApprovedBy = "alice",
            Action = "Ops/Actions/reboot-memex",
        };
        operation.ShapeRefusal(lane).Should().BeNull();
        operation.ShapeRefusal(lane with { Target = "Hosting/TriageItem" }).Should().Contain("names the target");

        var preparation = await operation.Prepare(Mesh, lane).Timeout(TestTimeouts.Convergence).Await(ct);
        preparation.Plan.Digest().Should().Be(MeshWeaver.Graph.ControlLane.RebootOperation.PlanFor("memex").Digest(),
            "the control side computes the same digest without asking the target");
        var line = await preparation.Execute().Timeout(TestTimeouts.Convergence).Await(ct);
        line.Should().Contain("[Reboot] filed " + InstanceRebootRequest.Namespace + "/");

        var filed = (await Reboots()).Should().ContainSingle().Subject;
        filed.Trigger.Should().Be(InstanceRebootTrigger.Person);
        filed.RequestedBy.Should().Be("alice");
    }

    private const string BabysitterPath = "Ops/Babysitter";

    private Task Stamp(string path, DateTimeOffset at, System.Threading.CancellationToken ct) =>
        Access.RunAsSystem(() => Mesh.ServiceProvider.GetRequiredService<IMeshService>().CreateOrUpdateNode(
                MeshNode.FromPath(path) with
                {
                    NodeType = "Markdown",
                    Content = System.Text.Json.JsonSerializer.SerializeToElement(new { lastRunAt = at }),
                }))
            .Timeout(TestTimeouts.Convergence).Await(ct);

    /// <summary>
    /// 🚨 Singletons must RESUME after the reboot: a pass stamped BEFORE the restart is not evidence — the
    /// check waits for a pass newer than the restart (green once one lands), stays RED naming the stale
    /// pass when none lands in time, and reports a singleton this instance does not have as not measured.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task ASingletonMustPassAfterTheRestart_AStalePassIsRed()
    {
        var ct = TestContext.Current.CancellationToken;
        var restart = DateTimeOffset.UtcNow;
        var request = new InstanceRebootRequest { Reason = "x", RestartRequestedAt = restart };
        var check = new SingletonsResumedRebootCheck();

        // Negative control: the last pass is 49 minutes before the restart, and nothing passes again.
        await Stamp(BabysitterPath, restart.AddMinutes(-49), ct);
        var stale = await check.Run(Mesh, request).Timeout(TestTimeouts.Convergence).Await(ct);
        stale.Outcome.Should().Be(InstanceRebootCheckOutcome.Failed);
        stale.Detail.Should().Contain("PR babysitter: Failed").And.Contain("did NOT resume")
            .And.Contain("PR review sweep: NotMeasured");

        // A fresh pass after the restart lands while the check waits → green.
        var waiting = check.Run(Mesh, request).Timeout(TestTimeouts.Convergence).Await(ct);
        await Stamp(BabysitterPath, DateTimeOffset.UtcNow.AddSeconds(1), ct);
        var resumed = await waiting;
        resumed.Outcome.Should().Be(InstanceRebootCheckOutcome.Passed, resumed.Detail ?? "");
        resumed.Detail.Should().Contain("PR babysitter: Passed");
    }

    [Fact(Timeout = 240_000)]
    public async Task AHealthyInstance_FilesNothing_AndOrdinaryFailuresAreNotAWedge()
    {
        var ct = TestContext.Current.CancellationToken;
        (await Watchdog.Evaluate(Mesh).Timeout(TestTimeouts.Convergence).Await(ct)).Decision.Should().Be(RebootWatchdogDecision.Healthy);

        // Negative control: five thread starts failing for a reason that is NOT a load/binding fault.
        for (var i = 0; i < 5; i++)
            Mesh.ReportWedgeFault(WedgeSignalKinds.ThreadStart, "test", new InvalidOperationException("the model refused")).Should().BeFalse();
        (await Watchdog.Evaluate(Mesh).Timeout(TestTimeouts.Convergence).Await(ct)).Decision.Should().Be(RebootWatchdogDecision.Healthy);
        (await Reboots()).Should().BeEmpty();
    }

    [Fact(Timeout = 240_000)]
    public async Task RepeatedBindingFaultsOnThreadStart_FireOneSelfReboot_ThenTheRateLimitHolds()
    {
        var ct = TestContext.Current.CancellationToken;
        for (var i = 0; i < 3; i++)
            Mesh.ReportWedgeFault(WedgeSignalKinds.ThreadStart, "Hosting/TriageIntake",
                new MissingMethodException("MeshWeaver.AI.ThreadPreparation", "set_Group")).Should().BeTrue();

        var fired = await Watchdog.Evaluate(Mesh).Timeout(TestTimeouts.Convergence).Await(ct);
        fired.Decision.Should().Be(RebootWatchdogDecision.Fired, fired.Detail);
        fired.Path.Should().StartWith(InstanceRebootRequest.Namespace + "/");

        var request = await Access.RunAsSystem(() => Mesh.GetMeshNodeStream(fired.Path!))
            .Select(n => n.ContentAs<InstanceRebootRequest>(Mesh.JsonSerializerOptions))
            .Where(r => r is not null)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        request!.Trigger.Should().Be(InstanceRebootTrigger.Watchdog);
        request.Wedged.Should().BeTrue();
        request.RequestedBy.Should().Be(RebootWatchdog.RequesterName);
        request.WedgeEvidence.Should().Contain("3 thread start(s) failed").And.Contain("MissingMethodException");

        // The same evidence a pass later: rate-limited, alarmed, nothing filed.
        var again = await Watchdog.Evaluate(Mesh).Timeout(TestTimeouts.Convergence).Await(ct);
        again.Decision.Should().Be(RebootWatchdogDecision.Suppressed);
        again.Detail.Should().Contain("rate-limited");
        (await Reboots()).Should().HaveCount(1, "at most one self-reboot per interval");
    }

    /// <summary>Writes a reboot request as System, exactly as <see cref="InstanceReboot.Request"/> would,
    /// but in a state the test chooses. Faulted: the executor and the per-process agent leave it alone.</summary>
    private async Task<string> FileFaulted(string id, DateTimeOffset faultedAt, System.Threading.CancellationToken ct)
    {
        // The boot reconcile pass re-arms a due Faulted request; let it finish before the request exists.
        await Mesh.ServiceProvider.GetRequiredService<RegistryUpdateReconciler>().BootReconciled
            .DefaultIfEmpty().Timeout(TestTimeouts.Convergence).Await(ct);
        var node = new MeshNode(id, InstanceRebootRequest.Namespace)
        {
            Name = "Reboot",
            NodeType = InstanceRebootRequest.NodeType,
            Content = new InstanceRebootRequest
            {
                Reason = "a reboot whose restart crashed",
                RequestedBy = "alice",
                RequestedAt = faultedAt.AddMinutes(-5),
                Status = InstanceRebootStatus.Faulted,
                FaultedAt = faultedAt,
                ProgressAt = faultedAt,
                Failure = "the restart request faulted: the hand-over failed",
                ExecutionWindow = TimeSpan.FromHours(3),
                Steps = InstanceReboot.FreshSteps(),
            },
        };
        await Access.RunAsSystem(() => Mesh.ServiceProvider.GetRequiredService<IMeshService>().CreateNode(node))
            .Timeout(TestTimeouts.Convergence).Await(ct);
        return node.Path;
    }

    private Task<InstanceRebootRequest> Read(string path, Func<InstanceRebootRequest, bool> until) =>
        Access.RunAsSystem(() => Mesh.GetMeshNodeStream(path))
            .Select(n => n.ContentAs<InstanceRebootRequest>(Mesh.JsonSerializerOptions))
            .Where(r => r is not null && until(r))
            .Select(r => r!)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

    /// <summary>
    /// 🚨 MeshWeaver#6625 — the incident's shape: ONE reboot request that stopped moving (its restart
    /// crashed hours ago and nothing re-armed it) held every pod's self-reboot back for as long as it
    /// existed. Now the watchdog judges it by its DECLARED execution window: stalled, so it fires (and
    /// alarms on the stalled request by name), and the reconcile pass ends the stalled request as
    /// Failed, naming the stall — it reaches a terminal state.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task AStalledRebootRequest_NoLongerHoldsTheSelfRebootBack_AndThePassEndsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var stalled = await FileFaulted("stalled-6625", DateTimeOffset.UtcNow.AddHours(-5), ct);

        for (var i = 0; i < 3; i++)
            Mesh.ReportWedgeFault(WedgeSignalKinds.ThreadStart, "Hosting/TriageIntake",
                new MissingMethodException("MeshWeaver.AI.ThreadPreparation", "set_Group")).Should().BeTrue();
        var pass = await Watchdog.Evaluate(Mesh).Timeout(TestTimeouts.Convergence).Await(ct);
        pass.Decision.Should().Be(RebootWatchdogDecision.Fired,
            "a request that recorded no progress within its declared window is ended by its owner, then released: " + pass.Detail);

        // The original was ENDED by its owner before the replacement was filed — not merely ignored.
        var after = await Read(stalled, _ => true);
        after.Status.Should().Be(InstanceRebootStatus.Failed);
        after.Failure.Should().Contain("stalled:").And.Contain("declared execution window of 180 min")
            .And.Contain("nothing re-armed it");
        after.CompletedAt.Should().NotBeNull();

        // 🚨 The ordering the review named: the next reconcile pass — re-arm, then the stall pass — must not
        // revive the original beside its replacement. Its re-arm is long due, so only its END prevents it.
        var now = DateTimeOffset.UtcNow;
        var rearmed = await InstanceReboot.RetryFaulted(Mesh, TimeSpan.FromMinutes(30), now)
            .Timeout(TestTimeouts.Convergence).Await(ct);
        rearmed.Should().NotContain(stalled, "an ended request is never re-armed, so it cannot roll beside the replacement");
        var asked = await InstanceReboot.FailStalled(Mesh, TimeSpan.FromHours(3), TimeSpan.FromMinutes(30), now)
            .Timeout(TestTimeouts.Convergence).Await(ct);
        asked.Should().NotContain(stalled);
        (await Read(stalled, _ => true)).Status.Should().Be(InstanceRebootStatus.Failed);
    }

    /// <summary>
    /// 🚨 #6625 review — a report lands BETWEEN the reader's evaluation and the commit: the reader saw the
    /// request stalled, a replica then reported, and only then is the end asked for. The owner judges its
    /// own current state, so the request stays open; the question is answered (cleared) either way.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task AReportLandingAfterTheEvaluation_KeepsTheRequestOpen()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = await FileFaulted("reported-6625", DateTimeOffset.UtcNow.AddHours(-5), ct);

        // The reader's evaluation: stalled.
        var seen = await Read(path, _ => true);
        InstanceReboot.Stalled(seen, DateTimeOffset.UtcNow, TimeSpan.FromHours(3), TimeSpan.FromMinutes(30)).Should().NotBeNull();

        // A replica reports after that evaluation…
        await Access.RunAsSystem(() => Mesh.GetMeshNodeStream(path).Update<InstanceRebootRequest>(r => r with
            {
                Replicas = r.Replicas.SetItem("late-pod", new InstanceRebootReplica
                {
                    Process = "late-pod", StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1), ReportedAt = DateTimeOffset.UtcNow,
                }),
            }))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        // …and only then is the end asked for.
        var asked = await InstanceReboot.RequestEndIfStalled(Mesh, path, DateTimeOffset.UtcNow)
            .Timeout(TestTimeouts.Convergence).Await(ct);

        // The owner's ANSWER is the version after the question that clears it.
        var answered = await Access.RunAsSystem(() => Mesh.GetMeshNodeStream(path))
            .Where(n => n.Version > asked.Version)
            .Select(n => n.ContentAs<InstanceRebootRequest>(Mesh.JsonSerializerOptions))
            .Where(r => r is not null && r.RequestedEndIfStalledAt is null)
            .Select(r => r!)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        answered.Status.Should().Be(InstanceRebootStatus.Faulted, "the owner saw the late report — not stalled");
        answered.Failure.Should().NotContain("stalled:");
        answered.Replicas.Should().ContainKey("late-pod");
    }

    /// <summary>
    /// 🚨 #6625 review — the request was ENDED (by the reconcile pass's question) BEFORE this watchdog read
    /// it. It is terminal, so it never goes to its owner again — but it is still NAMED: the pass lists it,
    /// read back from the persisted marker, and the Critical line is written for it.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task ARequestEndedBeforeTheWatchdogReadIt_IsStillNamed()
    {
        var ct = TestContext.Current.CancellationToken;
        var stalled = await FileFaulted("ended-first-6625", DateTimeOffset.UtcNow.AddHours(-5), ct);
        var asked = await InstanceReboot.FailStalled(Mesh, TimeSpan.FromHours(3), TimeSpan.FromMinutes(30), DateTimeOffset.UtcNow)
            .Timeout(TestTimeouts.Convergence).Await(ct);
        asked.Should().Equal([stalled]);
        (await Read(stalled, r => InstanceRebootStatus.IsTerminal(r.Status))).Status.Should().Be(InstanceRebootStatus.Failed);

        for (var i = 0; i < 3; i++)
            Mesh.ReportWedgeFault(WedgeSignalKinds.ThreadStart, "Hosting/TriageIntake",
                new MissingMethodException("MeshWeaver.AI.ThreadPreparation", "set_Group")).Should().BeTrue();
        var pass = await Watchdog.Evaluate(Mesh).Timeout(TestTimeouts.Convergence).Await(ct);
        pass.Decision.Should().Be(RebootWatchdogDecision.Fired, pass.Detail);
        pass.EndedStalled.Should().Contain(stalled, "a request another reader ended as stalled is still named");
    }

    /// <summary>The control: the same request, faulted a minute ago (its re-arm still ahead), IS in progress —
    /// it holds the self-reboot back, and the pass leaves it open.</summary>
    [Fact(Timeout = 240_000)]
    public async Task AProgressingRebootRequest_StillHoldsTheSelfRebootBack_AndThePassLeavesItOpen()
    {
        var ct = TestContext.Current.CancellationToken;
        var open = await FileFaulted("progressing-6625", DateTimeOffset.UtcNow.AddMinutes(-1), ct);

        for (var i = 0; i < 3; i++)
            Mesh.ReportWedgeFault(WedgeSignalKinds.ThreadStart, "Hosting/TriageIntake",
                new MissingMethodException("MeshWeaver.AI.ThreadPreparation", "set_Group")).Should().BeTrue();
        var pass = await Watchdog.Evaluate(Mesh).Timeout(TestTimeouts.Convergence).Await(ct);
        pass.Decision.Should().Be(RebootWatchdogDecision.Suppressed);
        pass.Detail.Should().Contain("already in progress").And.Contain(open);

        var ended = await InstanceReboot.FailStalled(Mesh, TimeSpan.FromHours(3), TimeSpan.FromMinutes(30), DateTimeOffset.UtcNow)
            .Timeout(TestTimeouts.Convergence).Await(ct);
        ended.Should().BeEmpty();
        (await Read(open, _ => true)).Status.Should().Be(InstanceRebootStatus.Faulted);
    }
}
