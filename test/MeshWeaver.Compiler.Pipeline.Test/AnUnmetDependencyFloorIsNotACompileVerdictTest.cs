using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// 🚨 <b>A replica that does not run what a NodeType's package requires forms no verdict about
/// it</b> — Systemorph/MeshWeaver#6195 (the 2026-10-28 recurrence on memex-cloud).
///
/// <para><b>What happened.</b> Hosting 1.73 declares <c>AI@^1.40.0</c>. During a roll, the
/// replicas of the outgoing generation still ran AI 1.39.8 — their own import had declined
/// Hosting on that ground for hours. At 04:54:51Z the <c>Hosting/Deployment</c> NodeType hub was
/// placed on one of them; it compiled the stored sources against AI 1.39.8 and failed with
/// <c>CS0246 FillOutcome</c> / <c>CS0103 NodeDescriptions</c>, members AI 1.40 ships. That verdict
/// was stamped <c>Error</c> on the ONE record every replica reads, the reboot watchdog declared the
/// instance WEDGED 20 minutes later, and only the next roll's boot bake cleared it. Nothing was
/// wrong with the code: the replica could not have compiled it.</para>
///
/// <para><b>The rule pinned here.</b> The compilation service asks
/// <see cref="INodeTypeDependencyFloor"/> before it reads, emits or loads anything; an unmet floor
/// ends the attempt with <see cref="DependencyFloorUnmetException"/>, an availability NON-verdict:
/// the record goes <see cref="CompilationStatus.Unavailable"/>, never <see cref="CompilationStatus.Error"/>,
/// and the last good build keeps its coordinates. The negative control is the identical compile
/// with the floor met, which must still build.</para>
/// </summary>
public class AnUnmetDependencyFloorIsNotACompileVerdictTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string HeldType = "type/FloorUnmetHeld";
    private const string FreeType = "type/FloorUnmetFree";

    private const string Unmet =
        "package 'Hosting' requires AI@^1.40.0, but this replica runs 'MeshWeaver.AI' (package 'AI') "
        + "at 1.39.8, which does not satisfy it";

    /// <summary>
    /// The seam's own test double: answers <see cref="Unmet"/> for <see cref="HeldType"/> and
    /// "met" for everything else — the production implementation (PluginCatalog's
    /// <c>NodeTypeDependencyFloorReader</c>) is pinned on its own, over the measured requirement.
    /// </summary>
    private sealed class FixedFloor : INodeTypeDependencyFloor
    {
        public IObservable<string?> Unmet(string nodeTypePath) =>
            Observable.Return<string?>(string.Equals(nodeTypePath, HeldType, StringComparison.Ordinal)
                ? AnUnmetDependencyFloorIsNotACompileVerdictTest.Unmet
                : null);
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services => services.AddSingleton<INodeTypeDependencyFloor>(new FixedFloor()));

    private IMeshNodeCompilationService Compiler =>
        Mesh.ServiceProvider.GetRequiredService<IMeshNodeCompilationService>();

    private static MeshNode TypeNode(string path) => MeshNode.FromPath(path) with
    {
        Name = path,
        NodeType = MeshNode.NodeTypePath,
        State = MeshNodeState.Active,
        LastModified = DateTimeOffset.UtcNow,
        Content = new NodeTypeDefinition
        {
            Description = "a type whose source compiles on any replica that runs its dependencies",
            Configuration = "config => config.WithContentType<FloorProbe>()",
        },
    };

    private static IReadOnlyList<MeshNode> Sources(string path) =>
    [
        new MeshNode("FloorProbe", $"{path}/Source")
        {
            NodeType = "Code",
            Name = "FloorProbe",
            State = MeshNodeState.Active,
            LastModified = DateTimeOffset.UtcNow,
            Content = new CodeConfiguration
            {
                Language = "csharp",
                Code = "public record FloorProbe { public string? Name { get; init; } }",
            },
        },
    ];

    /// <summary>What a compile attempt ended in: its result, or the fault it raised.</summary>
    private Task<(NodeCompilationResult? Result, Exception? Fault)> Attempt(string path, CancellationToken ct) =>
        Compiler.CompileAndGetConfigurations(TypeNode(path), Sources(path))
            .Take(1)
            .Select(result => (Result: result, Fault: (Exception?)null))
            .Catch((Exception ex) => Observable.Return(((NodeCompilationResult?)null, (Exception?)ex)))
            .Should().Within(TestTimeouts.Convergence)
            .Emit("a compile attempt must settle one way or the other",
                cancellationToken: ct);

    [Fact(Timeout = 300_000)]
    public async Task AnUnmetFloor_EndsTheAttemptBeforeRoslyn_AsANonVerdict()
    {
        var (result, fault) = await Attempt(HeldType, TestContext.Current.CancellationToken);

        result.Should().BeNull("the compile must not run on a replica that cannot satisfy the floor");
        var unmet = fault.Should().BeOfType<DependencyFloorUnmetException>(
            "an unmet floor is an availability fact about THIS replica (#6195)").Subject;
        unmet.NodeTypePath.Should().Be(HeldType);
        unmet.Unmet.Should().Be(Unmet);
        unmet.Message.Should().Contain("AI@^1.40.0").And.Contain("1.39.8",
            "the record's CompilationError carries this message — it must name what is unmet and what runs");

        NodeTypeCompilationHelpers.IsAvailabilityNonVerdict(unmet).Should().BeTrue(
            "the one predicate both the stamp and the park registry read — so the type is neither "
            + "stamped Error nor parked");
    }

    [Fact(Timeout = 300_000)]
    public async Task NegativeControl_TheSameCompileWithTheFloorMet_StillBuilds()
    {
        var (result, fault) = await Attempt(FreeType, TestContext.Current.CancellationToken);

        fault.Should().BeNull("the gate abstains when the floor is met — the compile runs as before");
        result.Should().NotBeNull();
        result!.AssemblyLocation.Should().NotBeNullOrEmpty(
            "the identical source compiles; only the floor differs between the two arms");
    }

    [Fact]
    public void TheStamp_IsUnavailable_AndKeepsTheLastGoodBuild()
    {
        // The record as a replica that MEETS the floor left it: a good build, Ok.
        var good = new NodeTypeDefinition
        {
            CompilationStatus = CompilationStatus.Compiling,
            LatestAssemblyCollection = "assemblies",
            LatestAssemblyPath = "Hosting_Deployment/good.dll",
            CurrentSourceVersions = ImmutableDictionary<string, long>.Empty.Add($"{HeldType}/Source/FloorProbe", 1),
        };

        var stamped = NodeTypeCompilationHelpers.ApplyCompileFailure(
            good, result: null, new DependencyFloorUnmetException(HeldType, Unmet), activityPath: null);

        stamped.CompilationStatus.Should().Be(CompilationStatus.Unavailable,
            "Error on the shared record is what the reboot watchdog counts and what every replica reads");
        stamped.LatestAssemblyPath.Should().Be(good.LatestAssemblyPath,
            "the build a satisfying replica compiled keeps serving the replicas that can load it");
        stamped.CompilationError.Should().Contain("AI@^1.40.0");

        // Negative control: a Roslyn verdict on the same record stays a verdict.
        NodeTypeCompilationHelpers.ApplyCompileFailure(
                good, result: null, new CompilationException(HeldType, "CS0246 FillOutcome"), activityPath: null)
            .CompilationStatus.Should().Be(CompilationStatus.Error);
    }
}
