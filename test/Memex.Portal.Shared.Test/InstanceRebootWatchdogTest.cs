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
}
