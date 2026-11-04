using System;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// 🚨 Issue #6195 — <b>a compile leg that runs out of its CPU-lane bound has formed no verdict about
/// the code, and must be retried.</b>
///
/// <para><b>The failure, measured on memex.systemorph.com (2026-11-03).</b> A Hosting source sync
/// made the prebuilt bundle's fingerprint stale, so three Hosting NodeTypes compiled locally, each
/// over a 302-file source closure whose Roslyn half takes about 240 s on a healthy replica. On a
/// replica spending about 20 % of wall time in GC pauses (<c>[LIVENESS] gcPause +2.02s</c> per 10 s
/// tick, a gen2 collection every tick) all three were admitted to the CPU lane at once and crossed
/// the 300 s bound. Each was stamped <c>Error</c> with <c>FailedBuildInputs</c> equal to the live
/// inputs, so the failed-verdict re-drive declined; each type also still held a last good build, so
/// the re-drive's never-built gate declined as well. Nothing retried them, and they stayed red for
/// about 7 h until a roll replaced the pods.</para>
///
/// <para>Each step is pinned here against a real <see cref="IoPool"/> and the real
/// <see cref="CpuLaneBound"/>, with no raised bound: (1) the expiry is typed; (2) it is classified as
/// a non-verdict and stamps <c>Unavailable</c>; (3) a type with a last good build that settled
/// <c>Unavailable</c> under the live inputs is re-driven; (4) a source <c>Error</c> under the same
/// inputs, and a framework-stale build, keep their old treatment.</para>
/// </summary>
public class LaneExpiryIsNotACompileVerdictTest
{
    private const string TypePath = "Hosting/InstanceAction";
    private static readonly TimeSpan Bound = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(20);

    private static readonly ImmutableDictionary<string, long> LiveSources =
        ImmutableDictionary<string, long>.Empty.SetItem($"{TypePath}/Source/InstanceActionContent", 1);

    /// <summary>
    /// The production shape of an admitted emit that does not finish inside the bound: it is
    /// cooperative, so the bound's cancellation stops it and frees the slot. The fault it ends in is
    /// what the terminal write-back classifies.
    /// </summary>
    private static Task<Exception> ExpireAnAdmittedEmit(IoPool lane, CancellationToken cancellationToken) =>
        CpuLaneBound.Bound(
                (ct, wait) => Observable.Defer(() =>
                {
                    wait.MarkQueued();
                    return lane.InvokeBlocking(laneCt =>
                    {
                        wait.MarkAdmitted();
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, laneCt);
                        SpinWait.SpinUntil(() => linked.Token.IsCancellationRequested, Generous);
                        return 0;
                    });
                }),
                Bound, "roslyn-compile", TypePath, () => lane.AdmissionsCompleted,
                laneState: () => new CpuLaneReading(lane.CurrentInFlight, lane.MaxConcurrency, lane.CurrentlyWaiting))
            .Materialize()
            .Where(n => n.Kind != System.Reactive.NotificationKind.OnNext)
            .Take(1)
            .Select(n => n.Exception ?? new InvalidOperationException("completed without a fault"))
            .Should().Within(Generous)
            .Emit("an admitted emit past the bound must expire", cancellationToken: cancellationToken);

    [Fact(Timeout = 60_000)]
    public async Task AnAdmittedEmitPastTheBound_IsANonVerdict_AndStampsUnavailable()
    {
        using var lane = new IoPool(1);
        var fault = await ExpireAnAdmittedEmit(lane, TestContext.Current.CancellationToken);

        fault.Should().BeOfType<CpuLaneExpiredException>()
            .Which.Should().BeAssignableTo<TimeoutException>("every reader that asks only 'was it a timeout' keeps its answer");
        fault.Message.Should().Contain("admitted to the CPU lane")
            .And.Contain("CPU lane 1/1 running", "the expiry must say what the lane held when it fired")
            .And.Contain("GC pause", "the expiry must say how much of the time the process was paused")
            .And.Contain("Unavailable");

        NodeTypeCompilationHelpers.IsAvailabilityNonVerdict(fault)
            .Should().BeTrue("the leg was cancelled before Roslyn formed any verdict");

        // 🚨 The activity line a person reads is ONE keyed sentence with the readings as arguments —
        // never the English Message spliced into a translated lead (#6195 review).
        var line = ((CpuLaneExpiredException)fault).ToActivityMessage();
        line.MessageKey.Should().Be("activity.compile.laneExpired.emit");
        var key = line.MessageKey!;
        foreach (var name in new[] { "leg", "path", "seconds", "running", "capacity", "queued",
                     "gcPausePercent", "gcPausedSeconds", "heapGiB", "gen2" })
            line.MessageArgs!.ContainsKey(name).Should().BeTrue($"the reading '{name}' rides as an argument");
        foreach (var locale in new[] { "en", "de" })
        {
            LocalizationCatalog.ContainsKey(key).Should().BeTrue();
            var rendered = LocalizationCatalog.GetNamed(
                key, locale, line.MessageArgs, fallback: null);
            rendered.Should().NotContain("{", $"every placeholder of the {locale} sentence is bound")
                .And.Contain(TypePath).And.Contain("1/1");
        }
        LocalizationCatalog
            .GetNamed(key, "de", line.MessageArgs, fallback: null)
            .Should().NotContain("did not finish").And.NotContain("At the expiry",
                "a German viewer reads no English platform prose");

        var stamped = NodeTypeCompilationHelpers.ApplyCompileFailure(
            BuiltType(CompilationStatus.Compiling), result: null, fault, activityPath: null, modulesHash: "mod-1");
        stamped.CompilationStatus.Should().Be(CompilationStatus.Unavailable);
        stamped.LatestAssemblyPath.Should().NotBeNullOrEmpty("the last good build keeps serving");
    }

    [Fact(Timeout = 60_000)]
    public async Task ALaneExpiry_OnATypeWithALastGoodBuild_IsReDriven_UnderTheVeryInputsItFailedUnder()
    {
        using var lane = new IoPool(1);
        var fault = await ExpireAnAdmittedEmit(lane, TestContext.Current.CancellationToken);
        var settled = NodeTypeCompilationHelpers.ApplyCompileFailure(
            BuiltType(CompilationStatus.Compiling), result: null, fault, activityPath: null, modulesHash: "mod-1");

        settled.FailedBuildInputs.Should().Be(
            NodeTypeCompilationHelpers.BuildInputsToken("mod-1", LiveSources),
            "the stamp records the live inputs, which is exactly why an Error here was never retried");
        NodeTypeCompilationHelpers.HasStaleFrameworkBuild(settled, modulesHash: null)
            .Should().BeFalse("the build is on this framework, so the framework-stale kickoff does not claim it");

        NodeTypeCompilationHelpers.HasStaleFailureVerdict(settled, "mod-1", TypePath)
            .Should().BeTrue("no verdict was formed, so re-asking is the only way to get one");

        var redriven = NodeTypeCompilationHelpers.ApplyFailedVerdictRedrive(
            new MeshNode("InstanceAction", "Hosting") { Content = settled }, TypePath, "mod-1", parkRegistry: null);
        ((NodeTypeDefinition)redriven.Content!).CompilationStatus.Should().Be(CompilationStatus.Pending);
    }

    [Fact]
    public void ASourceError_OnATypeWithALastGoodBuild_UnderTheSameInputs_StillDeclines()
    {
        var settled = NodeTypeCompilationHelpers.ApplyCompileFailure(
            BuiltType(CompilationStatus.Compiling), result: null,
            new CompilationException(TypePath, "CS0103: The name 'Nope' does not exist"), activityPath: null,
            modulesHash: "mod-1");

        settled.CompilationStatus.Should().Be(CompilationStatus.Error);
        NodeTypeCompilationHelpers.HasStaleFailureVerdict(settled, "mod-1", TypePath)
            .Should().BeFalse("a real verdict under the live inputs would reproduce identically");
    }

    [Fact]
    public void AnUnavailableSettle_OnAFrameworkStaleBuild_IsLeftToTheFrameworkStaleKickoff()
    {
        var def = BuiltType(CompilationStatus.Unavailable) with { CompiledFrameworkVersion = "0ldfr4me" };

        NodeTypeCompilationHelpers.HasStaleFrameworkBuild(def, modulesHash: null).Should().BeTrue();
        NodeTypeCompilationHelpers.HasStaleFailureVerdict(def, "mod-1", TypePath)
            .Should().BeFalse("two kickoffs claiming one state is how a rebuild loop starts");
    }

    /// <summary>
    /// 🚨 #6195 review — a framework-stale build that settled Unavailable belongs to the one-shot
    /// framework-stale kickoff until that kickoff has acted; once it has, it never looks again, so the
    /// failed-verdict re-drive takes it over instead of leaving it to an owner that has unsubscribed.
    /// </summary>
    [Fact]
    public void AnUnavailableSettle_OnAStaleBuild_PassesToTheReDrive_OnceTheStaleKickoffHasActed()
    {
        var def = BuiltType(CompilationStatus.Unavailable) with
        {
            CompiledFrameworkVersion = "0ldfr4me",
            FailedBuildInputs = NodeTypeCompilationHelpers.BuildInputsToken("mod-1", LiveSources),
        };

        NodeTypeCompilationHelpers.HasStaleFailureVerdict(def, "mod-1", TypePath, staleBuildKickoffSpent: false)
            .Should().BeFalse("the kickoff has not acted yet: it owns the stale build");
        NodeTypeCompilationHelpers.HasStaleFailureVerdict(def, "mod-1", TypePath, staleBuildKickoffSpent: true)
            .Should().BeTrue("the kickoff consumed its one shot, so nothing else would ever retry it");
    }

    /// <summary>
    /// 🚨 #6195 review — ownership is decided with the SAME full guards the framework-stale kickoff
    /// uses. A build on the current framework whose modules (legacy stamp) or recorded dependencies
    /// drifted is stale to that kickoff, so it is not claimed here until the kickoff has acted.
    /// </summary>
    [Theory]
    [InlineData("modules")]
    [InlineData("dependency")]
    public void ModuleOrDependencyDrift_IsOwnedByTheStaleKickoff_UnderTheFullGuards(string drift)
    {
        const string Toolchain = "mvid:toolchain-1";
        Func<string, string?> resolver(string id) => name => name == "Custom.Module" ? id : null;
        var record = drift == "dependency"
            ? MeshWeaver.Compiler.CompiledDependencies.Compute(["Custom.Module"], resolver("mvid:v1"), Toolchain)
            : null;
        var def = BuiltType(CompilationStatus.Unavailable) with
        {
            CompiledDependencies = record,
            CompiledModulesHash = drift == "modules" ? "mod-OLD" : null,
        };
        var guards = new NodeTypeCompilationHelpers.BuildGuards(
            ModulesHash: "mod-1", DependencyIdOf: resolver("mvid:v2"), ToolchainId: Toolchain);

        NodeTypeCompilationHelpers.HasStaleFrameworkBuild(def, guards)
            .Should().BeTrue($"the {drift} drift makes the build stale to the framework-stale kickoff");
        NodeTypeCompilationHelpers.HasStaleFailureVerdict(def, "mod-1", TypePath, guards)
            .Should().BeFalse("two kickoffs must never claim one state");
        NodeTypeCompilationHelpers.HasStaleFailureVerdict(def, "mod-1", TypePath, guards, staleBuildKickoffSpent: true)
            .Should().BeTrue("…until the stale kickoff has acted");

        // Negative control: with no drift the build is nobody else's, and the re-drive owns it.
        var undrifted = guards with { DependencyIdOf = resolver("mvid:v1") };
        NodeTypeCompilationHelpers.HasStaleFailureVerdict(
                def with { CompiledModulesHash = null }, "mod-1", TypePath, undrifted)
            .Should().BeTrue();
    }

    // A type that compiled successfully on THIS framework before: it holds a last good build.
    private static NodeTypeDefinition BuiltType(CompilationStatus status) => new()
    {
        Configuration = "config => config",
        Sources = ["namespace:Source scope:subtree"],
        CompilationStatus = status,
        CurrentSourceVersions = LiveSources,
        LatestAssemblyCollection = "assemblies",
        LatestAssemblyPath = "Hosting_InstanceAction/v41.dll",
        CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion,
        LastCompileSucceededAt = DateTimeOffset.UtcNow.AddDays(-1),
    };
}
