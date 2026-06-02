using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reflection;
using System.Threading.Tasks;
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
/// Integration tests for the NodeType compilation pipeline. Uses the real mesh
/// (no mock IMessageHub, no direct InMemoryStorageAdapter) — the NodeType
/// definition + its source code nodes are seeded via <see cref="IMeshService.CreateNode"/>,
/// and compilation is exercised by posting <see cref="GetCompilationPathRequest"/>
/// to the per-NodeType hub (the production flow).
/// </summary>
public class MeshNodeCompilationIntegrationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // NOTE: not opted into ShareMeshAcrossTests — CompileWithMultipleSourceLocationsPullsInExternalCode
    // expects fresh Source/Test Code nodes per [Fact]; under shared mesh, prior
    // [Fact]s leave only 1 of 3 expected nodes in the partition and the cross-
    // NodeType compile fails with "type or namespace 'Platform' could not be found".

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddGraph();

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    /// <summary>
    /// Reactive chain: create the NodeType + all its source Code nodes, then issue
    /// the GetCompilationPathRequest — all in one observable. Each create's response
    /// completes only after persistence commit, so the next step sees the node.
    /// Single ToTask at the test edge.
    /// </summary>
    private IObservable<GetCompilationPathResponse> CreateAndCompile(
        string nodeTypeId,
        NodeTypeDefinition definition,
        params (string Name, string Code)[] sources)
    {
        var nodeTypePath = $"type/{nodeTypeId}";
        var typeNode = MeshNode.FromPath(nodeTypePath) with
        {
            Name = nodeTypeId,
            NodeType = MeshNode.NodeTypePath,
            Content = definition,
            State = MeshNodeState.Active
        };

        // Create type-def → for each source, create code node sequentially → then compile.
        // SelectMany chains; each CreateNode emits AFTER persistence + change-feed completes.
        return MeshService.CreateNode(typeNode)
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
            .SelectMany(_ => MeshWeaver.Messaging.MessageHubExtensions.Observe(
                    Mesh,
                    (MeshWeaver.Messaging.IRequest<GetCompilationPathResponse>)new GetCompilationPathRequest(),
                    o => o.WithTarget(new Address(nodeTypePath))))
            .Select(d => d.Message);
    }

    [Fact]
    public void CompilesSimpleNodeTypeWithDefaultSources()
    {
        var response = CreateAndCompile("Story",
            new NodeTypeDefinition { Configuration = "config => config.WithContentType<Story>()" },
            ("code", @"
public record Story
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
}")).Should().Within(60.Seconds()).Emit();

        response.Success.Should().BeTrue($"compile should succeed; error: {response.Error}");
        response.AssemblyLocation.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void CompileFailsWhenSourceCodeIsInvalid()
    {
        var response = CreateAndCompile("Broken",
            new NodeTypeDefinition { Configuration = "config => config.WithContentType<Broken>()" },
            ("code", "public record Broken { this is not valid C# }")).Should().Within(60.Seconds()).Emit();

        response.Success.Should().BeFalse("compile should fail for invalid source");
        response.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void CompileWithMultipleSourceLocationsPullsInExternalCode()
    {
        // Post NodeType has Platform record under its Source.
        var postResponse = CreateAndCompile("Post",
            new NodeTypeDefinition { Configuration = "config => config.WithContentType<Post>()" },
            ("code", @"
public record Platform
{
    public string Id { get; init; } = string.Empty;
}
public record Post
{
    public string Id { get; init; } = string.Empty;
}")).Should().Within(60.Seconds()).Emit();
        postResponse.Success.Should().BeTrue($"Post should compile; error: {postResponse.Error}");

        // Wait for the catalog index to pick up Post's Source/code node before
        // Profile's compile queries `namespace:type/Post/Source scope:subtree`.
        // Query is a live change feed (fan-out baked in) — it emits the
        // Added change when the just-created Code node surfaces, so we just match
        // the first snapshot that contains it. No Interval poll.
        MeshService.Query<MeshNode>("namespace:type/Post/Source scope:subtree nodeType:Code")
            .Should().Within(15.Seconds()).Match(c => c.Items.Any(n => n.Path == "type/Post/Source/code"));

        // Profile NodeType references Platform via cross-NodeType Sources.
        var response = CreateAndCompile("Profile",
            new NodeTypeDefinition
            {
                Configuration = "config => config.WithContentType<Profile>()",
                Sources =
                [
                    "namespace:$self/Source scope:subtree",
                    "namespace:type/Post/Source scope:subtree"
                ]
            },
            ("code", @"
public record Profile
{
    public string Id { get; init; } = string.Empty;
    public Platform? Platform { get; init; }
}")).Should().Within(60.Seconds()).Emit();

        response.Success.Should().BeTrue($"Profile should compile with cross-NodeType Sources; error: {response.Error}");
        response.AssemblyLocation.Should().NotBeNullOrEmpty();

        var assembly = Assembly.LoadFrom(response.AssemblyLocation!);
        assembly.GetType("Profile").Should().NotBeNull();
        assembly.GetType("Platform").Should().NotBeNull("Platform from Post/Source must be included");
    }
}
