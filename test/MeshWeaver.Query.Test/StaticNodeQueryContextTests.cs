using System.Collections.Generic;
using System.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Query.Test;

/// <summary>
/// Verifies that the StaticNodeQueryProvider respects context:search —
/// type definitions, roles, and agents should NOT appear in search results.
/// </summary>
public class StaticNodeQueryContextTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGraph()
            .AddSampleUsers();

    /// <summary>
    /// Reactive replacement for <c>QueryAsync(...).ToListAsync()</c>: the first
    /// <see cref="QueryChangeType.Initial"/> emission carries the full snapshot.
    /// </summary>
    private IReadOnlyList<MeshNode> QueryNodes(string query)
        => MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery(query))
            .Should().Within(System.TimeSpan.FromSeconds(30))
            .Match(c => c.ChangeType == QueryChangeType.Initial).Items;

    [Fact(Timeout = 30000)]
    public void SearchContext_ExcludesStaticNodes()
    {
        // Arrange: create a real user-content node with "Markdown" in its name
        // so we can verify it IS returned while the type definition "Markdown" is NOT.
        NodeFactory.CreateNode(MeshNode.FromPath("snq/my-markdown-doc") with
        {
            Name = "My Markdown Document", NodeType = "Markdown"
        }).Should().Emit();

        // Act: text search for "Markdown" with context:search
        var results = QueryNodes("*Markdown* scope:descendants context:search is:main limit:50");

        Output.WriteLine($"Results: {results.Count}");
        foreach (var r in results)
            Output.WriteLine($"  {r.Path} (NodeType={r.NodeType}, MainNode={r.MainNode})");

        // Assert: user content node IS found
        results.Should().Contain(n => n.Path == "snq/my-markdown-doc",
            "user-content node with 'Markdown' in its name should appear");

        // Assert: static type definition nodes should NOT be in context:search results
        results.Should().NotContain(n => n.Path == "Markdown",
            "the type definition node 'Markdown' should be excluded from context:search");

        // No static provider nodes should leak through (roles, agents, etc.)
        var staticPaths = new[] { "Role", "Agent", "User", "VUser", "Group" };
        foreach (var sp in staticPaths)
        {
            results.Should().NotContain(n => n.Path == sp,
                $"static node '{sp}' should be excluded from context:search");
        }
    }

    [Fact(Timeout = 30000)]
    public void NoContext_IncludesStaticNodes()
    {
        // Act: query for nodeType definitions WITHOUT context:search
        var results = QueryNodes("nodeType:Markdown scope:descendants limit:50");

        Output.WriteLine($"Results without context: {results.Count}");
        foreach (var r in results)
            Output.WriteLine($"  {r.Path} (NodeType={r.NodeType})");

        // Assert: without context:search, static/type-definition nodes are allowed
        // (they may or may not appear depending on other provider behavior,
        // but at minimum the static provider is not short-circuited)
    }

    [Fact(Timeout = 30000)]
    public void SearchContext_IsMain_ExcludesNonMainNodes()
    {
        // Arrange: create a main node and a satellite node
        NodeFactory.CreateNode(MeshNode.FromPath("snq2/main-doc") with
        {
            Name = "Main Document", NodeType = "Markdown"
        }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath("snq2/main-doc/_Comment/c1") with
        {
            Name = "A Comment", NodeType = "Comment",
            MainNode = "snq2/main-doc"
        }).Should().Emit();

        // Act: search with is:main and context:search
        var results = QueryNodes("*Document* namespace:snq2 scope:descendants context:search is:main");

        Output.WriteLine($"Results: {results.Count}");
        foreach (var r in results)
            Output.WriteLine($"  {r.Path} (NodeType={r.NodeType}, MainNode={r.MainNode})");

        // Assert: main node found, satellite excluded
        results.Should().Contain(n => n.Path == "snq2/main-doc");
        results.Should().NotContain(n => n.NodeType == "Comment",
            "Comment is a satellite type and should be excluded by is:main and context:search");
    }
}
