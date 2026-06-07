using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Stability suite for the compile LEAF
/// (<see cref="IMeshNodeCompilationService.CompileAndGetConfigurations"/>) in
/// isolation from the NodeType-hub first-build kickoff / inline-dispatch machinery.
///
/// <para><c>CodeEditRecompileTest</c> intermittently stalls at its FIRST compile —
/// the kickoff activity sticks at "Invoking compiler" with no outcome (message trace
/// shows the source content is delivered, then silence). Roslyn runs off-hub
/// (<c>OnThreadPool</c>), so the message trace can't see it. This suite isolates the
/// question: does the compile LEAF itself ever fail to emit, or is the stall in the
/// inline-dispatch wiring (RunCompile on the NodeType hub action block)?</para>
///
/// <para>Each iteration is a FRESH NodeType (exercising the first-compile path the
/// kickoff hits) and drives the leaf DIRECTLY. If the leaf is sound, every iteration
/// emits within the bound; a stall surfaces as a per-iteration timeout that pins the
/// exact failing compile. If this suite is rock-solid across many iterations while
/// the full recompile test still flakes, the defect is in the dispatch, not the leaf.</para>
/// </summary>
public class CompileLeafStabilityTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddGraph();

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    [Fact(Timeout = 240_000)]
    public void CompileAndGetConfigurations_FreshNodeTypes_AllEmitWithinBound()
    {
        var compilationService = Mesh.ServiceProvider
            .GetRequiredService<IMeshNodeCompilationService>();
        const int iterations = 8;

        for (var i = 0; i < iterations; i++)
        {
            var nodeTypePath = $"type/LeafStab{i}";
            var typeNode = MeshNode.FromPath(nodeTypePath) with
            {
                Name = $"LeafStab{i}",
                NodeType = MeshNode.NodeTypePath,
                Content = new NodeTypeDefinition
                {
                    Configuration = $"config => config.WithContentType<LeafStab{i}>()"
                },
                State = MeshNodeState.Active
            };
            MeshService.CreateNode(typeNode).Should().Within(30.Seconds()).Emit(
                $"iteration {i}: NodeType create must emit");

            MeshService.CreateNode(new MeshNode("code", $"{nodeTypePath}/Source")
            {
                NodeType = "Code",
                Name = "code",
                Content = new CodeConfiguration
                {
                    Code = $"public record LeafStab{i} {{ public string Title {{ get; init; }} = string.Empty; }}",
                    Language = "csharp"
                },
                State = MeshNodeState.Active
            }).Should().Within(30.Seconds()).Emit($"iteration {i}: source create must emit");

            Output.WriteLine($"=== iteration {i}: driving compile leaf ===");

            // Drive the LEAF directly — bypasses the kickoff/inline-dispatch path.
            var result = compilationService.CompileAndGetConfigurations(typeNode)
                .Should().Within(25.Seconds()).Emit(
                    $"iteration {i}: the compile leaf must emit an outcome (no stall)");

            result.Should().NotBeNull($"iteration {i}: must return a result");
            result!.AssemblyLocation.Should().NotBeNullOrEmpty(
                $"iteration {i}: must produce an assembly; "
                + $"error: {result.Log?.Errors()?.FirstOrDefault()?.Message}");
            Output.WriteLine($"=== iteration {i}: OK ({result.AssemblyLocation}) ===");
        }
    }
}
