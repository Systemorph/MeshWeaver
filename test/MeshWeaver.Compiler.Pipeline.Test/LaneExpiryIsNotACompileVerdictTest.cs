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
                laneState: () => $"CPU lane {lane.CurrentInFlight}/{lane.MaxConcurrency} running, "
                                 + $"{lane.CurrentlyWaiting} queued")
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
        NodeTypeCompilationHelpers.CompileFailureLead(fault).Key.Should().Be("activity.compile.laneExpired");

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
