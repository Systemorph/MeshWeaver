using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using System.Reactive.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Data;
using MeshWeaver.Mesh.Activity;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Query.Test;

public class CatalogFallbackTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder);

    [Fact(Timeout = 10000)]
    public async Task Catalog_NoActivity_FallsBackToActualNodes()
    {
        // Arrange - create nodes but no activity
        await NodeFactory.CreateNode(MeshNode.FromPath("org/acme") with
        {
            Name = "Acme",
            NodeType = "Markdown"
        }).Should().Emit();
        await NodeFactory.CreateNode(MeshNode.FromPath("org/contoso") with
        {
            Name = "Contoso",
            NodeType = "Markdown"
        }).Should().Emit();

        // Act - query for organizations using standard query (simulating fallback)
        var results = (await MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery("path:org nodeType:Markdown scope:descendants limit:20")).Should().Match(c => c.ChangeType == QueryChangeType.Initial)).Items;

        // Assert - should return actual nodes when no activity
        results.Should().HaveCount(2);
        results.Select(n => n.Name).Should().Contain(["Acme", "Contoso"]);
    }

    [Fact(Timeout = 10000)]
    public async Task SourceActivity_ReturnsMainNodesOnly()
    {
        // Arrange - Create main content node and Activity satellite
        await NodeFactory.CreateNode(MeshNode.FromPath("org/alpha") with
        {
            Name = "Alpha",
            NodeType = "Markdown"
        }).Should().Emit();
        await NodeFactory.CreateNode(MeshNode.FromPath("org/alpha/_activity/log1") with
        {
            Name = "Activity Log 1",
            NodeType = "Activity",
            MainNode = "org/alpha",
            Content = new ActivityLog("DataUpdate") { HubPath = "org/alpha" }
        }).Should().Emit();

        // Act - source:activity returns main content nodes (not satellites)
        var results = (await MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery("source:activity namespace:org scope:descendants")).Should().Match(c => c.ChangeType == QueryChangeType.Initial)).Items;

        // Assert - returns the main node, not the Activity satellite
        results.Should().ContainSingle();
        results[0].Name.Should().Be("Alpha");
        results[0].NodeType.Should().Be("Markdown");
    }
}

public class CatalogSearchAndPaginationTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder);

    [Fact(Timeout = 10000)]
    public async Task Catalog_SearchWithQuery_FiltersResults()
    {
        // Arrange
        await NodeFactory.CreateNode(MeshNode.FromPath("org/acme") with
        {
            Name = "Acme Corporation",
            NodeType = "Markdown"
        }).Should().Emit();
        await NodeFactory.CreateNode(MeshNode.FromPath("org/contoso") with
        {
            Name = "Contoso Ltd",
            NodeType = "Markdown"
        }).Should().Emit();
        await NodeFactory.CreateNode(MeshNode.FromPath("org/fabrikam") with
        {
            Name = "Fabrikam Inc",
            NodeType = "Markdown"
        }).Should().Emit();

        // Act - query with filter for name containing "Corp" using wildcard operator
        var results = (await MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery("path:org nodeType:Markdown name:*Corp* scope:descendants limit:20")).Should().Match(c => c.ChangeType == QueryChangeType.Initial)).Items;

        // Assert - should only return Acme Corporation
        results.Should().ContainSingle();
        results.First().Name.Should().Be("Acme Corporation");
    }

    [Fact(Timeout = 10000)]
    public async Task Catalog_TextSearch_FiltersResults()
    {
        // Arrange
        await NodeFactory.CreateNode(MeshNode.FromPath("doc/report1") with
        {
            Name = "Annual Financial Report 2024",
            NodeType = "Markdown"
        }).Should().Emit();
        await NodeFactory.CreateNode(MeshNode.FromPath("doc/memo1") with
        {
            Name = "Team Meeting Notes",
            NodeType = "Markdown"
        }).Should().Emit();

        // Act - text search for "financial" with descendants scope
        var results = (await MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery("path:doc nodeType:Markdown financial scope:descendants limit:20")).Should().Match(c => c.ChangeType == QueryChangeType.Initial)).Items;

        // Assert
        results.Should().ContainSingle();
        results.First().Name.Should().Be("Annual Financial Report 2024");
    }

    [Fact(Timeout = 10000)]
    public async Task Catalog_Pagination_LoadsMoreItems()
    {
        // Arrange - create 10 items
        for (int i = 0; i < 10; i++)
        {
            await NodeFactory.CreateNode(MeshNode.FromPath($"item/item{i:D2}") with
            {
                Name = $"Item {i:D2}",
                NodeType = "Markdown"
            }).Should().Emit();
        }

        // Act - first page (3 items) with descendants scope
        var firstPage = (await MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery("path:item nodeType:Markdown scope:descendants limit:3")).Should().Match(c => c.ChangeType == QueryChangeType.Initial)).Items;

        // Load more (6 items total)
        var secondPage = (await MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery("path:item nodeType:Markdown scope:descendants limit:6")).Should().Match(c => c.ChangeType == QueryChangeType.Initial)).Items;

        // Load all (10 items)
        var allItems = (await MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery("path:item nodeType:Markdown scope:descendants limit:100")).Should().Match(c => c.ChangeType == QueryChangeType.Initial)).Items;

        // Assert
        firstPage.Should().HaveCount(3);
        secondPage.Should().HaveCount(6);
        allItems.Should().HaveCount(10);
    }

    [Fact(Timeout = 10000)]
    public async Task Catalog_HasMore_DetectedCorrectly()
    {
        // Arrange - create 5 items
        for (int i = 0; i < 5; i++)
        {
            await NodeFactory.CreateNode(MeshNode.FromPath($"test/node{i}") with
            {
                Name = $"Node {i}",
                NodeType = "Markdown"
            }).Should().Emit();
        }

        // Act - request limit+1 to detect if there are more
        var limit = 3;
        var queryLimit = limit + 1;
        var results = (await MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:test nodeType:Markdown scope:descendants limit:{queryLimit}")).Should().Match(c => c.ChangeType == QueryChangeType.Initial)).Items;

        var hasMore = results.Count > limit;
        if (hasMore)
        {
            results = results.Take(limit).ToList();
        }

        // Assert
        hasMore.Should().BeTrue();
        results.Should().HaveCount(3);
    }

    [Fact(Timeout = 10000)]
    public async Task Catalog_NodeTypeFilter_FiltersCorrectly()
    {
        // Arrange - Create nodes with different types
        await NodeFactory.CreateNode(MeshNode.FromPath("data/project1") with
        {
            Name = "Project Alpha",
            NodeType = "Code"
        }).Should().Emit();
        await NodeFactory.CreateNode(MeshNode.FromPath("data/doc1") with
        {
            Name = "Document One",
            NodeType = "Markdown"
        }).Should().Emit();
        await NodeFactory.CreateNode(MeshNode.FromPath("data/project2") with
        {
            Name = "Project Beta",
            NodeType = "Code"
        }).Should().Emit();

        // Act - query for Code nodes only
        var results = (await MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery("path:data nodeType:Code scope:descendants limit:20")).Should().Match(c => c.ChangeType == QueryChangeType.Initial)).Items;

        // Assert - should return only Code nodes, not the Markdown
        results.Should().HaveCount(2);
        results.Select(n => n.Name).Should().Contain(["Project Alpha", "Project Beta"]);
        results.Select(n => n.Name).Should().NotContain("Document One");
    }
}

/// <summary>
/// Tests that source:activity queries return main content nodes (not satellites).
/// source:activity adds IsMain=true filter in InMemory, INNER JOIN in PostgreSQL.
/// </summary>
public class SourceActivityQueryTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder);

    [Fact(Timeout = 10000)]
    public async Task SourceActivity_ReturnsMainNodesOnly()
    {
        // Arrange - create main content nodes and Activity satellites
        await NodeFactory.CreateNode(MeshNode.FromPath("org/alpha") with
        {
            Name = "Alpha",
            NodeType = "Markdown"
        }).Should().Emit();
        await NodeFactory.CreateNode(MeshNode.FromPath("org/alpha/_activity/log1") with
        {
            Name = "Activity Log 1",
            NodeType = "Activity",
            MainNode = "org/alpha",
            Content = new ActivityLog("DataUpdate") { HubPath = "org/alpha" }
        }).Should().Emit();
        await NodeFactory.CreateNode(MeshNode.FromPath("org/alpha/_activity/log2") with
        {
            Name = "Activity Log 2",
            NodeType = "Activity",
            MainNode = "org/alpha",
            Content = new ActivityLog("Approval") { HubPath = "org/alpha" }
        }).Should().Emit();

        // Act
        var results = (await MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery("source:activity scope:descendants")).Should().Match(c => c.ChangeType == QueryChangeType.Initial)).Items;

        // Assert - returns main content node, not Activity satellites
        results.Should().ContainSingle();
        results[0].Name.Should().Be("Alpha");
        results[0].NodeType.Should().Be("Markdown");
    }

    [Fact(Timeout = 10000)]
    public async Task SourceActivity_ExcludesSatelliteNodes()
    {
        // Arrange - create main nodes and satellites
        for (var i = 0; i < 5; i++)
        {
            await NodeFactory.CreateNode(MeshNode.FromPath($"org/node{i}") with
            {
                Name = $"Node {i}",
                NodeType = "Markdown"
            }).Should().Emit();
            await NodeFactory.CreateNode(MeshNode.FromPath($"org/node{i}/_activity/log{i}") with
            {
                Name = $"Activity {i}",
                NodeType = "Activity",
                MainNode = $"org/node{i}",
                Content = new ActivityLog("DataUpdate") { HubPath = $"org/node{i}" }
            }).Should().Emit();
        }

        // Act
        var results = (await MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery("source:activity scope:descendants limit:3")).Should().Match(c => c.ChangeType == QueryChangeType.Initial)).Items;

        // Assert - only main nodes returned, respects limit
        results.Should().HaveCount(3);
        results.Should().AllSatisfy(n => n.NodeType.Should().Be("Markdown"));
    }

    [Fact(Timeout = 10000)]
    public async Task SourceActivity_WithNamespaceFilter()
    {
        // Arrange - create main nodes with Activity satellites in different namespaces
        await NodeFactory.CreateNode(MeshNode.FromPath("projA/doc1") with
        {
            Name = "Doc 1",
            NodeType = "Markdown"
        }).Should().Emit();
        await NodeFactory.CreateNode(MeshNode.FromPath("projA/doc1/_activity/log1") with
        {
            Name = "Doc1 Activity",
            NodeType = "Activity",
            MainNode = "projA/doc1",
            Content = new ActivityLog("DataUpdate") { HubPath = "projA/doc1" }
        }).Should().Emit();
        await NodeFactory.CreateNode(MeshNode.FromPath("projB/doc2") with
        {
            Name = "Doc 2",
            NodeType = "Markdown"
        }).Should().Emit();
        await NodeFactory.CreateNode(MeshNode.FromPath("projB/doc2/_activity/log1") with
        {
            Name = "Doc2 Activity",
            NodeType = "Activity",
            MainNode = "projB/doc2",
            Content = new ActivityLog("Approval") { HubPath = "projB/doc2" }
        }).Should().Emit();

        // Act - filter by namespace
        var results = (await MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery("source:activity namespace:projA scope:descendants")).Should().Match(c => c.ChangeType == QueryChangeType.Initial)).Items;

        // Assert - returns only the main node under projA
        results.Should().ContainSingle();
        results[0].Name.Should().Be("Doc 1");
    }
}
