using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// The cold-start BATCH bake (<see cref="NodeTypeBatchBake.BakeOne"/>) compiles without the
/// activation path's settle, and publishes through its own gated stamp — which must carry the
/// compile's forward dependency edges too (<c>Doc/Architecture/DependencyGraph</c>). Before, every
/// type a cold pod baked this way had no edge set at all, and a later cache hit could not add one.
/// </summary>
public class ABatchBakeWritesItsDependencyEdgesTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly TimeSpan PerTypeBudget = TimeSpan.FromMinutes(3);

    private readonly string suffix = Guid.NewGuid().ToString("N")[..8];
    private string LibPath => $"{TestPartition}/Lib{suffix}";
    private string TypeId => $"Uses{suffix}";
    private string TypePath => $"{TestPartition}/{TypeId}";

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    [Fact(Timeout = 300_000)]
    public async Task ABatchBakedType_StoresItsEdgeSet_ForTheBuildItStamped()
    {
        var ct = TestContext.Current.CancellationToken;
        foreach (var node in new[]
                 {
                     new MeshNode("Thing", $"{LibPath}/Source")
                     {
                         NodeType = "Code",
                         State = MeshNodeState.Active,
                         Content = new CodeConfiguration { Language = "csharp", Code = $"public record LibThing{suffix}(int Value);" },
                     },
                     new MeshNode(TypeId, TestPartition)
                     {
                         NodeType = MeshNode.NodeTypePath,
                         Name = TypeId,
                         State = MeshNodeState.Active,
                         Content = new NodeTypeDefinition
                         {
                             Sources = ["namespace:Source scope:subtree", $"shared=@{LibPath}/Source"],
                         },
                     },
                     new MeshNode("Main", $"{TypePath}/Source")
                     {
                         NodeType = "Code",
                         State = MeshNodeState.Active,
                         Content = new CodeConfiguration
                         {
                             Language = "csharp",
                             Code = $"public static class Uses{suffix} {{ public static int Twice(LibThing{suffix} t) => t.Value * 2; }}",
                         },
                     },
                 })
            await MeshService.CreateNode(node).Take(1)
                .Should().Within(TestTimeouts.Convergence).Emit($"{node.Path} must exist before the sweep reads it", ct);

        var enumerated = await Observable.Interval(200.Milliseconds()).StartWith(0L)
            .SelectMany(_ => MeshService.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{TypePath}").AsSystem()).Take(1))
            .Select(change => DynamicTypePreWarmer.DynamicTypesOf(change.Items, Mesh.JsonSerializerOptions, null))
            .Where(types => types.Definitions.ContainsKey(TypePath))
            .FirstAsync()
            .Should().Within(TestTimeouts.Convergence).Emit("the sweep enumerates the type", ct);
        var sets = await NodeTypeBatchBake
            .ResolveSources(MeshService, Mesh.ServiceProvider.GetRequiredService<AccessService>(),
                enumerated.Definitions, [TypePath], null)
            .Should().Within(TestTimeouts.Convergence).Emit("the batched discovery pass establishes the set", ct);
        var outcome = await NodeTypeBatchBake
            .BakeOne(Mesh, enumerated.Nodes[TypePath], sets.TryGetValue(TypePath, out var set) ? set : [], PerTypeBudget, null)
            .Should().Within(PerTypeBudget + TimeSpan.FromMinutes(1)).Emit("BakeOne reaches one outcome", ct);
        outcome.Status.Should().Be(PreWarmStatus.Compiled, outcome.Detail ?? "(no detail)");

        // Existence from a listing, content from the node's own stream (CQRS: the set may not exist yet).
        var edgePath = DependencyEdgeSetPaths.PathFor(TypePath);
        await MeshService.Query<MeshNode>(
                MeshQueryRequest.FromQuery($"path:{DependencyEdgeSetPaths.NamespaceFor(TypePath)} scope:children").AsSystem())
            .Where(change => change.Items.Any(n => n.Path == edgePath))
            .Should().Within(TestTimeouts.CrossSilo).Emit("the batch stamp writes the edge set", ct);
        var edges = (await Mesh.GetMeshNodeStream(edgePath).Should().Within(TestTimeouts.CrossSilo)
                .Match(n => n?.ContentAs<DependencyEdgeSet>(Mesh.JsonSerializerOptions) is { BuildMvid: not null },
                    "the stored set names its build", cancellationToken: ct))
            .ContentAs<DependencyEdgeSet>(Mesh.JsonSerializerOptions)!;
        var definition = (await Mesh.GetMeshNodeStream(TypePath).Should().Within(TestTimeouts.Convergence)
                .Match(n => n?.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions) is { LatestAssemblyMvid: not null },
                    "the stamp names the build", cancellationToken: ct))
            .ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!;

        edges.Edges.Select(e => (e.Target, e.Kind)).Should().Equal([(LibPath, DependencyEdgeKinds.NodeType)]);
        edges.Edges[0].Evidence.Should().Equal([$"LibThing{suffix}"]);
        edges.BuildMvid.Should().Be(definition.LatestAssemblyMvid, "the set names the build the record names");
    }
}
