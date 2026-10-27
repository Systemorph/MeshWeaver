using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// The RUNTIME half of the forward dependency edges (<c>Doc/Architecture/DependencyGraph</c>): the
/// mesh's own NodeType compile reads the edges off its Roslyn compilation, stamps them with the
/// build it emitted, and the compile settle stores them at <c>{NodeTypePath}/_Dependency/Forward</c>.
/// </summary>
public class ACompileWritesItsDependencyEdgesTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string A = "deplib/A";
    private const string B = "depapp/B";

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private static MeshNode TypeNode(string path, IReadOnlyList<string>? sources = null) => MeshNode.FromPath(path) with
    {
        Name = path[(path.LastIndexOf('/') + 1)..],
        NodeType = MeshNode.NodeTypePath,
        State = MeshNodeState.Active,
        LastModified = DateTimeOffset.UtcNow,
        Content = new NodeTypeDefinition { Sources = sources },
    };

    private static MeshNode Code(string ownerPath, string id, string code) => new(id, $"{ownerPath}/Source")
    {
        NodeType = "Code",
        Name = id,
        State = MeshNodeState.Active,
        LastModified = DateTimeOffset.UtcNow,
        Content = new CodeConfiguration { Language = "csharp", Code = code },
    };

    private static MeshNode AThing() => Code(A, "AThing", """
        /// <summary>A type owned by deplib/A.</summary>
        public record DepAThing(int Value);
        """);

    private static MeshNode UsesA() => Code(B, "UsesA", """
        /// <summary>B's own code, binding A's type.</summary>
        public static class DepUsesA
        {
            /// <summary>Doubles it.</summary>
            public static DepAThing Twice(DepAThing a) => new(a.Value * 2);
        }
        """);

    private static readonly string[] BSources = ["namespace:Source scope:subtree", $"shared=@{A}/Source"];

    [Fact(Timeout = 300_000)]
    public async Task TheRuntimeCompile_CarriesTheEdgeBToA_StampedWithTheBuildItLoaded()
    {
        var result = await Mesh.ServiceProvider.GetRequiredService<IMeshNodeCompilationService>()
            .CompileAndGetConfigurations(TypeNode(B, BSources), [AThing(), UsesA()])
            .Take(1)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the compile must settle", cancellationToken: TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result!.AssemblyLocation.Should().NotBeNullOrEmpty(string.Join("; ", result.Log?.Messages.Select(m => m.Message) ?? []));
        var edges = result.DependencyEdges?.GraphEdges();
        edges.Should().NotBeNull("a compile that ran Roslyn here always carries its edges");
        edges!.Source.Should().Be(B);
        var edge = edges.Edges.Should().ContainSingle().Which;
        edge.Target.Should().Be(A);
        edge.Kind.Should().Be(DependencyEdgeKinds.NodeType);
        edge.Evidence.Should().Equal(["DepAThing"]);
        edges.BuildMvid.Should().NotBeNullOrEmpty("the edges name the build they were read from");
        edges.BuildMvid.Should().Be(ServedBuildIdentity.OfFile(result.AssemblyLocation) ?? edges.BuildMvid,
            "on disk the stamp is the very value the settle writes as LatestAssemblyMvid");
    }

    [Fact(Timeout = 300_000)]
    public async Task NegativeControl_ARuntimeCompileThatBindsNothingForeign_CarriesAnEmptySet()
    {
        var result = await Mesh.ServiceProvider.GetRequiredService<IMeshNodeCompilationService>()
            .CompileAndGetConfigurations(TypeNode(B, BSources), [AThing(), Code(B, "Plain", """
                /// <summary>Binds nothing of A's.</summary>
                public record DepPlain(int Value);
                """)])
            .Take(1)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the compile must settle", cancellationToken: TestContext.Current.CancellationToken);

        result!.DependencyEdges.Should().NotBeNull();
        result.DependencyEdges!.GraphEdges().Edges.Should().BeEmpty("no NodeType or Module edge — the platform edges every skeleton makes are not this test's subject");
        result.DependencyEdges!.IncludedWithoutUse.Should().Equal([A]);
    }

    [Fact(Timeout = 300_000)]
    public async Task TheSettle_StoresTheEdgeSetUnderTheType_ForTheBuildItStamped()
    {
        using (Mesh.ServiceProvider.GetRequiredService<MeshWeaver.Messaging.AccessService>().ImpersonateAsSystem())
        {
            foreach (var node in new[] { TypeNode(A), AThing(), UsesA(), TypeNode(B, BSources) })
                await MeshService.CreateNode(node).Should().Within(TestTimeouts.Convergence)
                    .Emit(cancellationToken: TestContext.Current.CancellationToken);
        }

        var compiled = await Mesh.GetMeshNodeStream(B).Should().Within(TestTimeouts.CrossSilo)
            .Match(n => n?.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions) is { } d
                        && d.CompilationStatus is CompilationStatus.Ok or CompilationStatus.Error,
                "B compiles", cancellationToken: TestContext.Current.CancellationToken);
        var definition = compiled.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!;
        definition.CompilationStatus.Should().Be(CompilationStatus.Ok, definition.CompilationError ?? "");

        // The edge set may not exist yet when B reads Ok (the settle writes it AFTER the stamp), and a
        // point read of an absent node is a routing NotFound — so EXISTENCE comes from a children
        // listing, and only then the CONTENT from the node's own stream (AGENTS.md, CQRS).
        var edgePath = DependencyEdgeSetPaths.PathFor(B);
        await MeshService.Query<MeshNode>(
                MeshQueryRequest.FromQuery($"path:{DependencyEdgeSetPaths.NamespaceFor(B)} scope:children").AsSystem())
            .Where(change => change.Items.Any(n => n.Path == edgePath))
            .Should().Within(TestTimeouts.CrossSilo)
            .Emit("the settle writes the edge set after the stamp", cancellationToken: TestContext.Current.CancellationToken);
        var stored = await Mesh.GetMeshNodeStream(edgePath).Should().Within(TestTimeouts.CrossSilo)
            .Match(n => n?.ContentAs<DependencyEdgeSet>(Mesh.JsonSerializerOptions) is { BuildMvid: not null },
                "the settle writes the edge set after the stamp", cancellationToken: TestContext.Current.CancellationToken);

        stored.NodeType.Should().Be(DependencyEdgeSetPaths.NodeType);
        var edges = stored.ContentAs<DependencyEdgeSet>(Mesh.JsonSerializerOptions)!.GraphEdges();
        edges.Source.Should().Be(B);
        edges.Edges.Select(e => (e.Target, e.Kind)).Should().Equal([(A, DependencyEdgeKinds.NodeType)]);
        edges.BuildMvid.Should().Be(definition.LatestAssemblyMvid,
            "the stored set names the build the type record names — that equality is what lets a "
            + "reader tell a current edge set from a stale one");
    }
}
