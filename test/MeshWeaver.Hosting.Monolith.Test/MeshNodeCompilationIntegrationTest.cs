using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Reflection;
using System.Threading.Tasks;
using FluentAssertions;
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
/// (no mock IMessageHub, no direct InMemoryPersistenceService) — the NodeType
/// definition + its source code nodes are seeded via <see cref="IMeshService.CreateNode"/>,
/// and compilation is exercised by posting <see cref="GetCompilationPathRequest"/>
/// to the per-NodeType hub (the production flow).
/// </summary>
public class MeshNodeCompilationIntegrationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddGraph();

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    /// <summary>
    /// Reactive chain: create the NodeType + all its source Code nodes, then issue
    /// the GetCompilationPathRequest — all in one observable. Each create's response
    /// completes only after persistence commit, so the next step sees the node.
    /// Single ToTask at the test edge.
    /// </summary>
    private Task<GetCompilationPathResponse> CreateAndCompile(
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
            .Select(d => d.Message)
            .FirstAsync()
            .ToTask(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CompilesSimpleNodeTypeWithDefaultSources()
    {
        var response = await CreateAndCompile("Story",
            new NodeTypeDefinition { Configuration = "config => config.WithContentType<Story>()" },
            ("code", @"
public record Story
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
}"));

        response.Success.Should().BeTrue($"compile should succeed; error: {response.Error}");
        response.AssemblyLocation.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CompileFailsWhenSourceCodeIsInvalid()
    {
        var response = await CreateAndCompile("Broken",
            new NodeTypeDefinition { Configuration = "config => config.WithContentType<Broken>()" },
            ("code", "public record Broken { this is not valid C# }"));

        response.Success.Should().BeFalse("compile should fail for invalid source");
        response.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CompileWithMultipleSourceLocationsPullsInExternalCode()
    {
        // Post NodeType has Platform record under its Source.
        var postResponse = await CreateAndCompile("Post",
            new NodeTypeDefinition { Configuration = "config => config.WithContentType<Post>()" },
            ("code", @"
public record Platform
{
    public string Id { get; init; } = string.Empty;
}
public record Post
{
    public string Id { get; init; } = string.Empty;
}"));
        postResponse.Success.Should().BeTrue($"Post should compile; error: {postResponse.Error}");

        // Profile NodeType references Platform via cross-NodeType Sources.
        var response = await CreateAndCompile("Profile",
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
}"));

        response.Success.Should().BeTrue($"Profile should compile with cross-NodeType Sources; error: {response.Error}");
        response.AssemblyLocation.Should().NotBeNullOrEmpty();

        var assembly = Assembly.LoadFrom(response.AssemblyLocation!);
        assembly.GetType("Profile").Should().NotBeNull();
        assembly.GetType("Platform").Should().NotBeNull("Platform from Post/Source must be included");
    }
}
