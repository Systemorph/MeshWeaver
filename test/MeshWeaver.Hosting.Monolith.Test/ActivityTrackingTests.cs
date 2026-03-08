using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Activity;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Tests for activity tracking via IMeshNodePersistence (UserActivity nodes).
/// Activity is tracked at the navigation level (ApplicationPage), not at the persistence layer.
/// </summary>
public class ActivityTrackingTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder);
    private async Task CreateUserActivityNodeAsync(string userId, string path, string? name = null, string? nodeType = null, ActivityType type = ActivityType.Read)
    {
        var now = System.DateTimeOffset.UtcNow;
        var encodedPath = path.Replace("/", "_");
        var activityPath = $"_useractivity/{userId}/{encodedPath}";
        var record = new UserActivityRecord
        {
            Id = encodedPath,
            NodePath = path,
            UserId = userId,
            ActivityType = type,
            FirstAccessedAt = now,
            LastAccessedAt = now,
            AccessCount = 1,
            NodeName = name,
            NodeType = nodeType,
        };
        var activityNode = MeshNode.FromPath(activityPath) with
        {
            NodeType = "UserActivity",
            Name = name ?? path,
            State = MeshNodeState.Active,
            Content = record
        };
        await NodeFactory.CreateNodeAsync(activityNode);
    }

    [Fact(Timeout = 10000)]
    public async Task SaveActivities_TracksReadActivity()
    {
        // Arrange
        var userId = "user-123";

        // Act
        await CreateUserActivityNodeAsync(userId, "org/acme", "Acme Corp", "Markdown");

        // Assert
        var node = await MeshQuery.QueryAsync<MeshNode>($"path:_useractivity/{userId}/org_acme scope:exact").FirstOrDefaultAsync();
        node.Should().NotBeNull();
        var activity = node!.Content as UserActivityRecord;
        activity.Should().NotBeNull();
        activity!.NodePath.Should().Be("org/acme");
        activity.UserId.Should().Be(userId);
        activity.ActivityType.Should().Be(ActivityType.Read);
        activity.AccessCount.Should().Be(1);
    }

    [Fact(Timeout = 10000)]
    public async Task SaveActivities_TracksWriteActivity()
    {
        // Arrange
        var userId = "user-456";

        // Act
        await CreateUserActivityNodeAsync(userId, "org/contoso", "Contoso Ltd", "Markdown", ActivityType.Write);

        // Assert
        var node = await MeshQuery.QueryAsync<MeshNode>($"path:_useractivity/{userId}/org_contoso scope:exact").FirstOrDefaultAsync();
        node.Should().NotBeNull();
        var activity = node!.Content as UserActivityRecord;
        activity.Should().NotBeNull();
        activity!.NodePath.Should().Be("org/contoso");
        activity.ActivityType.Should().Be(ActivityType.Write);
    }
}

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
    public async Task Catalog_WithActivity_CanLoadNodesFromActivityPaths()
    {
        // Arrange - Create nodes
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("org/alpha") with
        {
            Name = "Alpha",
            NodeType = "Markdown"
        });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("org/beta") with
        {
            Name = "Beta",
            NodeType = "Markdown"
        });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("org/gamma") with
        {
            Name = "Gamma",
            NodeType = "Markdown"
        });

        // Act - query using source:activity (returns all matching nodes in InMemory mode)
        var results = await MeshQuery.QueryAsync<MeshNode>("source:activity nodeType:Markdown namespace:org")
            .ToListAsync();

        // Assert - all matching nodes are returned
        results.Should().HaveCount(3);
        results.Select(n => n.Name).Should().Contain(["Alpha", "Beta", "Gamma"]);
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
/// Tests that source:activity queries work via IMeshQuery.
/// InMemory doesn't support SQL JOIN, so source:activity is treated like a normal query
/// (returns all matching nodes without activity ordering). Activity ordering is only
/// available with PostgreSQL provider.
/// </summary>
public class InMemoryActivityOrderedQueryTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder);

    [Fact(Timeout = 10000)]
    public async Task SourceActivity_ReturnsAllMatchingNodes()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("org/alpha") with
        {
            Name = "Alpha",
            NodeType = "Markdown"
        });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("org/beta") with
        {
            Name = "Beta",
            NodeType = "Markdown"
        });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("org/gamma") with
        {
            Name = "Gamma",
            NodeType = "Markdown"
        });

        // Act - source:activity query via InMemory (filters still apply, ordering is default)
        var results = await MeshQuery.QueryAsync<MeshNode>("source:activity nodeType:Markdown namespace:org")
            .ToListAsync();

        // Assert - all matching nodes are returned (source:activity is stripped by parser)
        results.Should().HaveCount(3);
        results.Select(n => n.Name).Should().Contain(["Alpha", "Beta", "Gamma"]);
    }

    [Fact(Timeout = 10000)]
    public async Task SourceActivity_RespectsNodeTypeFilter()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("data/proj1") with
        {
            Name = "Project One",
            NodeType = "Code"
        });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("data/doc1") with
        {
            Name = "Document One",
            NodeType = "Markdown"
        });

        // Act
        var results = await MeshQuery.QueryAsync<MeshNode>("source:activity nodeType:Code namespace:data")
            .ToListAsync();

        // Assert - only Code nodes returned
        results.Should().ContainSingle();
        results[0].Name.Should().Be("Project One");
    }

    [Fact(Timeout = 10000)]
    public async Task SourceActivity_RespectsLimit()
    {
        // Arrange
        for (var i = 0; i < 5; i++)
        {
            await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"items/item{i}") with
            {
                Name = $"Item {i}",
                NodeType = "Markdown"
            });
        }

        // Act
        var results = await MeshQuery.QueryAsync<MeshNode>("source:activity nodeType:Markdown namespace:items limit:3")
            .ToListAsync();

        // Assert
        results.Should().HaveCount(3);
    }
}
