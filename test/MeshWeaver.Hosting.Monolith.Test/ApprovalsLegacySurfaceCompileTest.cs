using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pins the LEGACY Approvals surface for in-mesh sources: <c>MeshWeaver.Graph.ApprovalExtensions</c>
/// with <c>AddApprovals(this MessageHubConfiguration)</c>. The Approvals extraction (#1654) moved
/// the class to the MeshWeaver.Approvals namespace and narrowed the public entry to MeshBuilder —
/// and the first image shipping that regressed 11 NodeTypes across the SocialMedia and UWDeepfield
/// partitions to CompileError (CS1061 <c>AddApprovals</c> / CS0103 <c>ApprovalExtensions</c>),
/// which the bake gate answered by refusing readiness mesh-wide. In-mesh source is invisible to
/// <c>dotnet build</c>, so THIS test is the compiler for those callers: it compiles a NodeType
/// whose Source uses the exact legacy shape and must stay green for as long as any partition's
/// content does.
/// </summary>
public class ApprovalsLegacySurfaceCompileTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    /// <summary>Same shape as <c>CellSurfaceScriptingSeamTest.CreateAndCompile</c>.</summary>
    private async Task<NodeTypeDefinition> CreateAndCompile(
        string nodeTypePath,
        NodeTypeDefinition definition,
        params (string Name, string Code)[] sources)
    {
        var typeNode = MeshNode.FromPath(nodeTypePath) with
        {
            Name = nodeTypePath.Split('/').Last(),
            NodeType = MeshNode.NodeTypePath,
            Content = definition,
            State = MeshNodeState.Active
        };

        await MeshService.CreateNode(typeNode)
            .SelectMany(_ => sources
                .Select(source => MeshService.CreateNode(new MeshNode(source.Name, $"{nodeTypePath}/Source")
                {
                    NodeType = "Code",
                    Name = source.Name,
                    Content = new CodeConfiguration { Code = source.Code, Language = "csharp" },
                    State = MeshNodeState.Active
                }))
                .Aggregate(Observable.Return<MeshNode?>(null), (chain, next) =>
                    chain.SelectMany(_ => next.Select(n => (MeshNode?)n))))
            .Should().Within(30.Seconds()).Emit();

        var node = await Mesh.GetMeshNodeStream(nodeTypePath)
            .Should().Within(60.Seconds())
            .Match(n => n?.Content is NodeTypeDefinition d
                && d.CompilationStatus is CompilationStatus.Ok or CompilationStatus.Error);
        return (NodeTypeDefinition)node.Content!;
    }

    [Fact(Timeout = 120_000)]
    public async Task LegacyApprovalsSurface_StaysCompilableFromNodeSource()
    {
        // The EXACT caller shape live in the SocialMedia and UWDeepfield partitions:
        // `using MeshWeaver.Graph;` + `configuration.AddApprovals()` + the class referenced
        // by its short name (the partition-path const). Both failed on the extraction image.
        var def = await CreateAndCompile(
            "type/LegacyApprovals",
            new NodeTypeDefinition
            {
                Configuration = "config => config"
            },
            ("wiring", """
                using MeshWeaver.Graph;
                using MeshWeaver.Messaging;

                public static class LegacyApprovalsWiring
                {
                    public const string Partition = ApprovalExtensions.ApprovalPartition;

                    public static MessageHubConfiguration Wire(MessageHubConfiguration configuration)
                        => configuration.AddApprovals();
                }
                """));

        def.CompilationStatus.Should().Be(CompilationStatus.Ok,
            "the legacy MeshWeaver.Graph Approvals surface has in-mesh callers the compiler "
            + $"cannot see — it must never disappear again; error: {def.CompilationError}");
    }
}
