using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Data;
using MeshWeaver.Mesh.Activity;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

public class CatalogFallbackTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder);

    [Fact(Timeout = 10000)]
    public async Task Catalog_NoActivity_FallsBackToActualNodes()
    {
        // Arrange - create nodes but no activity
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("org/acme") with
        {
            Name = "Acme",
            NodeType = "Markdown"
        });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("org/contoso") with
        {
            Name = "Contoso",
            NodeType = "Markdown"
        });

        // Act - query for organizations using standard query (simulating fallback)
        var results = await MeshQuery.QueryAsync<MeshNode>("path:org nodeType:Markdown scope:descendants limit:20")
            .ToListAsync();

        // Assert - should return actual nodes when no activity
        results.Should().HaveCount(2);
        results.Select(n => n.Name).Should().Contain(["Acme", "Contoso"]);
    }

    [Fact(Timeout = 10000)]
    public async Task SourceActivity_ReturnsMainNodesOnly()
    {
        // Arrange - Create main content node and Activity satellite
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("org/alpha") with
        {
            Name = "Alpha",
            NodeType = "Markdown"
        });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("org/alpha/_activity/log1") with
        {
            Name = "Activity Log 1",
            NodeType = "Activity",
            MainNode = "org/alpha",
            Content = new ActivityLog("DataUpdate") { HubPath = "org/alpha" }
        });

        // Act - source:activity returns main content nodes (not satellites)
        var results = await MeshQuery.QueryAsync<MeshNode>("source:activity namespace:org scope:descendants")
            .ToListAsync();

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
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("org/acme") with
        {
            Name = "Acme Corporation",
            NodeType = "Markdown"
        });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("org/contoso") with
        {
            Name = "Contoso Ltd",
            NodeType = "Markdown"
        });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("org/fabrikam") with
        {
            Name = "Fabrikam Inc",
            NodeType = "Markdown"
        });

        // Act - query with filter for name containing "Corp" using wildcard operator
        var results = await MeshQuery.QueryAsync<MeshNode>("path:org nodeType:Markdown name:*Corp* scope:descendants limit:20")
            .ToListAsync();

        // Assert - should only return Acme Corporation
        results.Should().ContainSingle();
        results.First().Name.Should().Be("Acme Corporation");
    }

    [Fact(Timeout = 10000)]
    public async Task Catalog_TextSearch_FiltersResults()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("doc/report1") with
        {
            Name = "Annual Financial Report 2024",
            NodeType = "Markdown"
        });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("doc/memo1") with
        {
            Name = "Team Meeting Notes",
            NodeType = "Markdown"
        });

        // Act - text search for "financial" with descendants scope
        var results = await MeshQuery.QueryAsync<MeshNode>("path:doc nodeType:Markdown financial scope:descendants limit:20")
            .ToListAsync();

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
            await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"item/item{i:D2}") with
            {
                Name = $"Item {i:D2}",
                NodeType = "Markdown"
            });
        }

        // Act - first page (3 items) with descendants scope
        var firstPage = await MeshQuery.QueryAsync<MeshNode>("path:item nodeType:Markdown scope:descendants limit:3")
            .ToListAsync();

        // Load more (6 items total)
        var secondPage = await MeshQuery.QueryAsync<MeshNode>("path:item nodeType:Markdown scope:descendants limit:6")
            .ToListAsync();

        // Load all (10 items)
        var allItems = await MeshQuery.QueryAsync<MeshNode>("path:item nodeType:Markdown scope:descendants limit:100")
            .ToListAsync();

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
            await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"test/node{i}") with
            {
                Name = $"Node {i}",
                NodeType = "Markdown"
            });
        }

        // Act - request limit+1 to detect if there are more
        var limit = 3;
        var queryLimit = limit + 1;
        var results = await MeshQuery.QueryAsync<MeshNode>($"path:test nodeType:Markdown scope:descendants limit:{queryLimit}")
            .ToListAsync();

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
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("data/project1") with
        {
            Name = "Project Alpha",
            NodeType = "Code"
        });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("data/doc1") with
        {
            Name = "Document One",
            NodeType = "Markdown"
        });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("data/project2") with
        {
            Name = "Project Beta",
            NodeType = "Code"
        });

        // Act - query for Code nodes only
        var results = await MeshQuery.QueryAsync<MeshNode>("path:data nodeType:Code scope:descendants limit:20")
            .ToListAsync();

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
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("org/alpha") with
        {
            Name = "Alpha",
            NodeType = "Markdown"
        });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("org/alpha/_activity/log1") with
        {
            Name = "Activity Log 1",
            NodeType = "Activity",
            MainNode = "org/alpha",
            Content = new ActivityLog("DataUpdate") { HubPath = "org/alpha" }
        });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("org/alpha/_activity/log2") with
        {
            Name = "Activity Log 2",
            NodeType = "Activity",
            MainNode = "org/alpha",
            Content = new ActivityLog("Approval") { HubPath = "org/alpha" }
        });

        // Act
        var results = await MeshQuery.QueryAsync<MeshNode>("source:activity scope:descendants")
            .ToListAsync();

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
            await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"org/node{i}") with
            {
                Name = $"Node {i}",
                NodeType = "Markdown"
            });
            await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"org/node{i}/_activity/log{i}") with
            {
                Name = $"Activity {i}",
                NodeType = "Activity",
                MainNode = $"org/node{i}",
                Content = new ActivityLog("DataUpdate") { HubPath = $"org/node{i}" }
            });
        }

        // Act
        var results = await MeshQuery.QueryAsync<MeshNode>("source:activity scope:descendants limit:3")
            .ToListAsync();

        // Assert - only main nodes returned, respects limit
        results.Should().HaveCount(3);
        results.Should().AllSatisfy(n => n.NodeType.Should().Be("Markdown"));
    }

    [Fact(Timeout = 10000)]
    public async Task SourceActivity_WithNamespaceFilter()
    {
        // Arrange - create main nodes with Activity satellites in different namespaces
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("projA/doc1") with
        {
            Name = "Doc 1",
            NodeType = "Markdown"
        });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("projA/doc1/_activity/log1") with
        {
            Name = "Doc1 Activity",
            NodeType = "Activity",
            MainNode = "projA/doc1",
            Content = new ActivityLog("DataUpdate") { HubPath = "projA/doc1" }
        });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("projB/doc2") with
        {
            Name = "Doc 2",
            NodeType = "Markdown"
        });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("projB/doc2/_activity/log1") with
        {
            Name = "Doc2 Activity",
            NodeType = "Activity",
            MainNode = "projB/doc2",
            Content = new ActivityLog("Approval") { HubPath = "projB/doc2" }
        });

        // Act - filter by namespace
        var results = await MeshQuery.QueryAsync<MeshNode>("source:activity namespace:projA scope:descendants")
            .ToListAsync();

        // Assert - returns only the main node under projA
        results.Should().ContainSingle();
        results[0].Name.Should().Be("Doc 1");
    }
}
