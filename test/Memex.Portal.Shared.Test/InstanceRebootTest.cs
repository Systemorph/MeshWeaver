#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
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
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The pure rules of an instance reboot (<c>Doc/Architecture/InstanceReboot</c>): what a request may
/// say, which replica reports count, and why a verification that measured no smoke check is RED.
/// Each rule with its negative control beside it.
/// </summary>
public class InstanceRebootRulesTest
{
    private static InstanceRebootReplica Replica(string process, DateTimeOffset started, params InstanceRebootCheck[] checks) => new()
    {
        Process = process,
        StartedAt = started,
        ReportedAt = started.AddSeconds(5),
        Image = "3.0.0-ci.9412",
        Checks = [.. checks],
    };

    private static InstanceRebootCheck Check(string name, string outcome, string? detail = null) =>
        new() { Name = name, Outcome = outcome, Detail = detail };

    [Fact]
    public void AReboot_NeedsAReason_AndAKnownTrigger()
    {
        InstanceReboot.Validate(new InstanceRebootRequest { Reason = "x" }).Should().BeNull();
        InstanceReboot.Validate(new InstanceRebootRequest { Reason = " " }).Should().Contain("reason");
        InstanceReboot.Validate(new InstanceRebootRequest { Reason = "x", Trigger = "Cron" }).Should().Contain("not a reboot trigger");
    }

    [Fact]
    public void OnlyAProcessBootedAfterTheRestart_Counts_AndASmokeFailureIsRedByName()
    {
        var restart = new DateTimeOffset(2026, 10, 5, 6, 0, 0, TimeSpan.Zero);
        var request = new InstanceRebootRequest { Reason = "x", RestartRequestedAt = restart };
        var green = Check(InstanceReboot.SmokePrefix + "thread-start", InstanceRebootCheckOutcome.Passed);

        // The old pod, reporting on its way out — not evidence.
        var old = request with { Replicas = ImmutableDictionary<string, InstanceRebootReplica>.Empty.Add("old", Replica("old", restart.AddHours(-1), green)) };
        InstanceReboot.EvaluateVerification(old, _ => true).Should().BeNull();

        var booted = old with { Replicas = old.Replicas.Add("new", Replica("new", restart.AddMinutes(2), green)) };
        InstanceReboot.EvaluateVerification(booted, _ => true)!.Passed.Should().BeTrue();

        // Negative control: the same booted process, its smoke check red.
        var red = old with
        {
            Replicas = old.Replicas.Add("new", Replica("new", restart.AddMinutes(2),
                Check(InstanceReboot.SmokePrefix + "thread-start", InstanceRebootCheckOutcome.Failed, "MissingMethodException: x"))),
        };
        var verdict = InstanceReboot.EvaluateVerification(red, _ => true)!;
        verdict.Passed.Should().BeFalse();
        verdict.Detail.Should().Contain("new: smoke:thread-start — MissingMethodException: x");
    }

    [Fact]
    public void AVerificationThatMeasuredNoSmokeCheck_IsRed_AndAnUnregisteredHealthCheckIsNamedNotGreen()
    {
        var restart = DateTimeOffset.UtcNow.AddMinutes(-5);
        var request = new InstanceRebootRequest
        {
            Reason = "x",
            RestartRequestedAt = restart,
            Replicas = ImmutableDictionary<string, InstanceRebootReplica>.Empty.Add("p", Replica("p", restart.AddMinutes(1),
                Check("health:nodetype_bake", InstanceRebootCheckOutcome.NotMeasured, "not registered"))),
        };
        var verdict = InstanceReboot.EvaluateVerification(request, _ => true)!;
        verdict.Passed.Should().BeFalse("no smoke check was measured — whether a thread can start is not established");
        verdict.Detail.Should().Contain("no smoke check was measured").And.Contain("health:nodetype_bake not measured");

        // Control: the same report WITH a measured smoke check is green, and still names the unmeasured one.
        var withSmoke = request with
        {
            Replicas = request.Replicas.SetItem("p", request.Replicas["p"] with
            {
                Checks = request.Replicas["p"].Checks.Add(Check(InstanceReboot.SmokePrefix + "thread-start", InstanceRebootCheckOutcome.Passed)),
            }),
        };
        var green = InstanceReboot.EvaluateVerification(withSmoke, _ => true)!;
        green.Passed.Should().BeTrue();
        green.Detail.Should().Contain("health:nodetype_bake not measured");
    }

    [Fact]
    public void TheBakeCheck_IsRedOnlyForACriticalNamespace()
    {
        var critical = ImmutableArray.Create("Hosting");
        NodeTypeBakeRebootCheck.Judge("b", MeshWeaver.Hosting.BakePhase.Regressed, "d",
                [new("Hosting/TriageItem", "Regressed: CS0117")], critical)
            .Outcome.Should().Be(InstanceRebootCheckOutcome.Failed);
        // Control: the same failure outside the critical namespaces is named, not red.
        var other = NodeTypeBakeRebootCheck.Judge("b", MeshWeaver.Hosting.BakePhase.Regressed, "d",
            [new("Edu/Quiz", "Regressed: CS0117")], critical);
        other.Outcome.Should().Be(InstanceRebootCheckOutcome.Passed);
        other.Detail.Should().Contain("Edu/Quiz");
        // "Hostingfoo" is not under "Hosting".
        NodeTypeBakeRebootCheck.Judge("b", MeshWeaver.Hosting.BakePhase.Regressed, "d",
                [new("HostingX/Thing", "Regressed")], critical)
            .Outcome.Should().Be(InstanceRebootCheckOutcome.Passed);
    }
}

/// <summary>
/// 🚨 <b>Reboot, end to end through the real lanes</b> — the real registry client, landing,
/// reconciler and module-reload resolve-and-land (a fake HTTP registry), the real request node,
/// executor and per-process agent, and the real <see cref="SelfUpdateHostedService"/> as the image
/// and restart path (only its Kubernetes updater is recorded).
///
/// <para>What is simulated, and only that: which generation the PROCESS booted after the restart has
/// loaded, and the thread-start smoke check that process runs — a stand-in that throws the incident's
/// <c>MissingMethodException</c> when the process still binds the old module generation (the real
/// smoke check, which starts a thread, ships with the AI module in MeshWeaver.Plugins).</para>
/// </summary>
public abstract class InstanceRebootScenario(ITestOutputHelper output) : ModuleReloadScenario(output)
{
    /// <summary>What the process booted after the restart has loaded: module → generation leaf.</summary>
    private ImmutableDictionary<string, string> bootedLoads = ImmutableDictionary<string, string>.Empty;

    protected virtual IDeploymentUpdater RebootUpdater => Updater;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder).AddUpdatePolicyType().ConfigureServices(services => services
            .AddSingleton(new InstanceRebootOptions
            {
                BakeSettleBudget = TimeSpan.FromSeconds(5),
                CheckBudget = TestTimeouts.Convergence,
                WatchdogEnabled = false,
            })
            .AddSingleton<IInstanceRebootActivation>(CreateActivation)
            .AddSingleton<IInstanceRebootCheck>(new ThreadStartStandIn(this)));

    /// <summary>The image + restart path of the reboot: the real self-updater on the recorded updater.</summary>
    protected virtual IInstanceRebootActivation CreateActivation(IServiceProvider sp) =>
        new SelfUpdateHostedService(sp.GetRequiredService<IMessageHub>(), new NoTags(), RebootUpdater, new SelfUpdateOptions());

    /// <summary>The update policy the Image step reads — Stable, so the empty tag listing admits nothing newer.</summary>
    protected async Task PolicyExists(CancellationToken ct) =>
        await UpdatePolicyNodeType.EnsureExists(Mesh, Access, UpdatePolicyKind.Stable)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

    protected async Task<string> Reboot(CancellationToken ct)
    {
        var ticket = await InstanceReboot.Request(Mesh, new InstanceRebootRequest
            {
                Reason = "the control instance cannot start a thread (the 2026-10-05 incident shape)",
                RequestedBy = "test-admin",
            })
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        ticket.Refusal.Should().BeNull();
        return ticket.Path!;
    }

    protected Task<InstanceRebootRequest> AwaitReboot(string path, Func<InstanceRebootRequest, bool> until, CancellationToken ct) =>
        Access.RunAsSystem(() => Mesh.GetMeshNodeStream(path))
            .Select(node => node.ContentAs<InstanceRebootRequest>(Mesh.JsonSerializerOptions))
            .Where(r => r is not null && until(r))
            .Select(r => r!)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);

    /// <summary>The process the restart created boots, loading <paramref name="generation"/>, and reports its checks.</summary>
    protected async Task BootedProcessReports(string path, DateTimeOffset restart, string generation, CancellationToken ct)
    {
        ImmutableInterlocked.Update(ref bootedLoads, l => l.SetItem(Module, generation));
        var booted = new InstanceRebootAgent { StartedAt = restart.AddSeconds(30), ProcessOf = _ => "restarted-pod" };
        await booted.Handle(Mesh, path).DefaultIfEmpty().Timeout(TestTimeouts.Convergence).Await(ct);
    }

    protected sealed class NoTags : IAcrTagLister
    {
        public Task<IReadOnlyList<string>> ListTagsAsync(string repository, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    /// <summary>
    /// The stand-in thread-start smoke: a thread start binds against the module the process loaded; when
    /// that is not the generation the activation record names, the start throws what the incident threw.
    /// </summary>
    private sealed class ThreadStartStandIn(InstanceRebootScenario scenario) : IInstanceRebootCheck
    {
        public string Name => InstanceReboot.SmokePrefix + "thread-start";

        public IObservable<InstanceRebootCheck> Run(IMessageHub meshHub, InstanceRebootRequest request) => Observable.Defer(() =>
        {
            var head = scenario.Head();
            var loads = scenario.bootedLoads.TryGetValue(Module, out var leaf) ? leaf : null;
            return Observable.Return(string.Equals(loads, head.Directory, StringComparison.Ordinal)
                ? new InstanceRebootCheck { Name = Name, Outcome = InstanceRebootCheckOutcome.Passed, Detail = $"a thread started on {Module} {head.Version}" }
                : new InstanceRebootCheck
                {
                    Name = Name,
                    Outcome = InstanceRebootCheckOutcome.Failed,
                    Detail = "MissingMethodException: Method not found: 'Void MeshWeaver.AI.ThreadPreparation.set_Group(System.String)'",
                });
        });
    }
}

/// <summary>The reboot's acceptance scenarios: a module store copy older than published.</summary>
public class InstanceRebootTest(ITestOutputHelper output) : InstanceRebootScenario(output)
{
    /// <summary>
    /// 🚨 THE ACCEPTANCE SCENARIO. M@1.1.0 is landed and running, 1.2.0 is published with a floor this
    /// platform meets. A reboot syncs (nothing to sync — named), lands 1.2.0, finds no newer image
    /// (named), takes exactly ONE restart (inside the roll floor, which a reboot does not wait on), and
    /// once the process booted after it — loading 1.2.0 — reports that a thread starts, it is Done.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task AnOlderModuleStoreCopy_IsLandedRestartedAndVerified_InOneReboot()
    {
        var ct = TestContext.Current.CancellationToken;
        await RunningVersionOne(ct);
        await PolicyExists(ct);
        Registry.Serve("1.2.0", floor: FloorFixture.Below);

        var path = await Reboot(ct);
        var waiting = await AwaitReboot(path, r => r.Status == InstanceRebootStatus.AwaitingRestart
            && r.Steps.Any(s => s.Name == InstanceRebootSteps.Restart && s.Outcome == InstanceRebootStepOutcome.Ok), ct);

        Step(waiting, InstanceRebootSteps.Sync).Outcome.Should().Be(InstanceRebootStepOutcome.Skipped);
        Step(waiting, InstanceRebootSteps.Sync).Detail.Should().Contain("no module source to sync");
        Step(waiting, InstanceRebootSteps.Modules).Outcome.Should().Be(InstanceRebootStepOutcome.Ok);
        waiting.Modules.Single().TargetVersion.Should().Be("1.2.0");
        waiting.Modules.Single().Landed.Should().BeTrue();
        Step(waiting, InstanceRebootSteps.Image).Outcome.Should().Be(InstanceRebootStepOutcome.Skipped, Step(waiting, InstanceRebootSteps.Image).Detail ?? "");
        Step(waiting, InstanceRebootSteps.Restart).Detail.Should().StartWith(RebootActivationKinds.Restarted);
        Updater.Restarts.Should().Be(1, "exactly one restart, not deferred by the roll floor");
        Head().Version.Should().Be("1.2.0", "the activation record names N+1 for the boot that follows");

        await BootedProcessReports(path, waiting.RestartRequestedAt!.Value, Head().Directory!, ct);

        var done = await AwaitReboot(path, r => InstanceRebootStatus.IsTerminal(r.Status), ct);
        done.Status.Should().Be(InstanceRebootStatus.Done, done.Failure ?? string.Join(" | ", done.Log));
        Step(done, InstanceRebootSteps.Verify).Outcome.Should().Be(InstanceRebootStepOutcome.Ok);
        done.Replicas["restarted-pod"].Checks.Should().Contain(c =>
            c.Name == InstanceReboot.SmokePrefix + "thread-start" && c.Outcome == InstanceRebootCheckOutcome.Passed);
        done.RequestedBy.Should().Be("test-admin", "the person's call is the signature and is recorded");
        Updater.Restarts.Should().Be(1);
    }

    /// <summary>
    /// The negative control: the process booted after the restart still binds the OLD generation — the
    /// thread start throws the incident's MissingMethodException and the reboot is RED, naming the check.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task AProcessThatStillLoadsTheOldModule_TurnsTheRebootRed_NamingTheThreadStart()
    {
        var ct = TestContext.Current.CancellationToken;
        await RunningVersionOne(ct);
        var oldGeneration = Head().Directory!;
        await PolicyExists(ct);
        Registry.Serve("1.2.0", floor: FloorFixture.Below);

        var path = await Reboot(ct);
        var waiting = await AwaitReboot(path, r => r.Status == InstanceRebootStatus.AwaitingRestart
            && r.Steps.Any(s => s.Name == InstanceRebootSteps.Restart && s.Outcome == InstanceRebootStepOutcome.Ok), ct);

        await BootedProcessReports(path, waiting.RestartRequestedAt!.Value, oldGeneration, ct);

        var red = await AwaitReboot(path, r => InstanceRebootStatus.IsTerminal(r.Status), ct);
        red.Status.Should().Be(InstanceRebootStatus.Failed);
        Step(red, InstanceRebootSteps.Verify).Outcome.Should().Be(InstanceRebootStepOutcome.Failed);
        red.Failure.Should().Contain("Verify:").And.Contain("smoke:thread-start").And.Contain("MissingMethodException");
    }

    /// <summary>A floor above the running platform is DECLINED by name on the Modules step — not red — and N keeps serving.</summary>
    [Fact(Timeout = 240_000)]
    public async Task ANewerModuleAboveTheFloor_IsDeclinedByName_AndTheRebootStillRestarts()
    {
        var ct = TestContext.Current.CancellationToken;
        await RunningVersionOne(ct);
        await PolicyExists(ct);
        Registry.Serve("1.2.0", floor: FloorFixture.Above);

        var path = await Reboot(ct);
        var waiting = await AwaitReboot(path, r => r.Status == InstanceRebootStatus.AwaitingRestart
            && r.Steps.Any(s => s.Name == InstanceRebootSteps.Restart && s.Outcome == InstanceRebootStepOutcome.Ok), ct);

        var modules = Step(waiting, InstanceRebootSteps.Modules);
        modules.Outcome.Should().Be(InstanceRebootStepOutcome.Ok);
        modules.Detail.Should().Contain("declined").And.Contain(FloorFixture.Above);
        Head().Version.Should().Be("1.1.0", "N keeps serving");
        Updater.Restarts.Should().Be(1);
    }

    internal static InstanceRebootStep Step(InstanceRebootRequest r, string name) => r.Steps.Single(s => s.Name == name);
}

/// <summary>A step that fails is reported RED with its name — here the restart path cannot roll anything.</summary>
public class InstanceRebootFailedStepTest(ITestOutputHelper output) : InstanceRebootScenario(output)
{
    private readonly CannotRestart cannot = new();

    protected override IDeploymentUpdater RebootUpdater => cannot;

    [Fact(Timeout = 240_000)]
    public async Task ARestartThatCannotBeTaken_IsRedByName_AndNothingWaitsForAVerification()
    {
        var ct = TestContext.Current.CancellationToken;
        await RunningVersionOne(ct);
        await PolicyExists(ct);

        var path = await Reboot(ct);
        var red = await AwaitReboot(path, r => InstanceRebootStatus.IsTerminal(r.Status), ct);

        red.Status.Should().Be(InstanceRebootStatus.Failed);
        var restart = InstanceRebootTest.Step(red, InstanceRebootSteps.Restart);
        restart.Outcome.Should().Be(InstanceRebootStepOutcome.Failed);
        restart.Detail.Should().StartWith(RebootActivationKinds.Unavailable);
        red.Failure.Should().Contain("the restart could not be requested");
        cannot.Attempts.Should().Be(1, "asked exactly once");
        // Control: the steps before it are not painted red by the failure.
        InstanceRebootTest.Step(red, InstanceRebootSteps.Modules).Outcome.Should().Be(InstanceRebootStepOutcome.Ok);
    }

    private sealed class CannotRestart : IDeploymentUpdater
    {
        private int attempts;
        public int Attempts => Volatile.Read(ref attempts);
        public bool CanPatch => true;
        public Task<DateTimeOffset?> LastRolledAtAsync(CancellationToken ct) => Task.FromResult<DateTimeOffset?>(null);
        public Task PatchToVersionAsync(string versionTag, CancellationToken ct) => Task.CompletedTask;

        public Task<bool> RestartAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref attempts);
            return Task.FromResult(false);
        }

        public Task<RolloutStrategyReading?> ReadRolloutStrategyAsync(CancellationToken ct) =>
            Task.FromResult<RolloutStrategyReading?>(new("RollingUpdate", "1", "0", 2));
    }
}

/// <summary>
/// 🚨 Keep serving: a reboot's roll is issued only when the portal Deployment's rollout cannot take it below
/// its serving replicas (maxSurge ≥ 1, maxUnavailable 0) — otherwise the Restart step is REFUSED by name and
/// nothing is rolled. The acceptance scenario (non-disruptive strategy, one restart) is its control.
/// </summary>
public class InstanceRebootDisruptiveRolloutTest(ITestOutputHelper output) : InstanceRebootScenario(output)
{
    [Fact(Timeout = 240_000)]
    public async Task ADisruptiveRolloutStrategy_RefusesTheRestart_ByName_AndRollsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        Updater.Strategy = new RolloutStrategyReading("RollingUpdate", "25%", "25%", 4);
        await RunningVersionOne(ct);
        await PolicyExists(ct);

        var path = await Reboot(ct);
        var red = await AwaitReboot(path, r => InstanceRebootStatus.IsTerminal(r.Status), ct);

        red.Status.Should().Be(InstanceRebootStatus.Failed);
        var restart = InstanceRebootTest.Step(red, InstanceRebootSteps.Restart);
        restart.Outcome.Should().Be(InstanceRebootStepOutcome.Failed);
        restart.Detail.Should().StartWith(RebootActivationKinds.Refused).And.Contain("maxUnavailable resolves to 1 of 4");
        Updater.Restarts.Should().Be(0, "a roll that would drop serving pods is never issued");
    }
}

/// <summary>
/// 🚨 The self-hand-over loop: a CONTROL instance (its hand-over route is its own inbox) that cannot
/// self-patch must not hand its own restart to its own control lane — refused loudly, nothing handed over.
/// </summary>
public class InstanceRebootControlSelfHandoverTest(ITestOutputHelper output) : InstanceRebootScenario(output)
{
    protected override IInstanceRebootActivation CreateActivation(IServiceProvider sp) =>
        new ControlInstanceThatCannotPatch(sp.GetRequiredService<IMessageHub>(), new NoTags(), Updater);

    [Fact(Timeout = 240_000)]
    public async Task AControlInstanceThatCannotSelfPatch_RefusesToHandItsRestartToItself()
    {
        var ct = TestContext.Current.CancellationToken;
        await RunningVersionOne(ct);
        await PolicyExists(ct);

        var path = await Reboot(ct);
        var red = await AwaitReboot(path, r => InstanceRebootStatus.IsTerminal(r.Status), ct);

        red.Status.Should().Be(InstanceRebootStatus.Failed);
        var restart = InstanceRebootTest.Step(red, InstanceRebootSteps.Restart);
        restart.Detail.Should().StartWith(RebootActivationKinds.Refused).And.Contain("IS the control instance").And.Contain("nothing was handed over");
        Updater.Restarts.Should().Be(0);
    }

    /// <summary>The real self-updater, configured as the control instance (local inbox route) with patching off.</summary>
    private sealed class ControlInstanceThatCannotPatch(IMessageHub hub, IAcrTagLister tags, IDeploymentUpdater updater)
        : SelfUpdateHostedService(hub, tags, updater, new SelfUpdateOptions { CanPatch = false })
    {
        private readonly SelfUpdateHandover local = new LocalRoute(hub);

        protected override SelfUpdateHandover ResolveHandover() => local;

        private sealed class LocalRoute(IMessageHub hub) : SelfUpdateHandover(hub)
        {
            public override Settings ReadSettings() =>
                new("memex", null, SecretPresent: false, LocalTargetListed: true, LocalSecretPresent: true, "https://memex.example")
                {
                    LocalSecretKey = LocalSecretKey,
                };
        }
    }
}

/// <summary>The rollout rule, pure — each refusal with its control.</summary>
public class RolloutStrategyRuleTest
{
    [Fact]
    public void OnlyASurgingRollWithNoUnavailablePods_IsNonDisruptive()
    {
        RolloutStrategyReading.NonDisruptiveRefusal(new("RollingUpdate", "1", "0", 2)).Should().BeNull();
        RolloutStrategyReading.NonDisruptiveRefusal(new("RollingUpdate", "25%", "0", 4)).Should().BeNull("25% of 4 rounds UP to a surge of 1");
        RolloutStrategyReading.NonDisruptiveRefusal(new(null, "1", "25%", 1)).Should().BeNull("25% of 1 rounds DOWN to 0 unavailable");
        RolloutStrategyReading.NonDisruptiveRefusal(null).Should().Contain("could not be read");
        RolloutStrategyReading.NonDisruptiveRefusal(new("Recreate", null, null, 2)).Should().Contain("Recreate");
        RolloutStrategyReading.NonDisruptiveRefusal(new("RollingUpdate", null, null, 4)).Should().Contain("maxUnavailable resolves to 1 of 4");
        RolloutStrategyReading.NonDisruptiveRefusal(new("RollingUpdate", "0", "0", 2)).Should().Contain("maxSurge resolves to 0");
        RolloutStrategyReading.NonDisruptiveRefusal(new("RollingUpdate", "x", "0", 2)).Should().Contain("could not be resolved");
    }
}
