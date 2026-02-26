using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Hosting.Activity;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Activity;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Tests for activity tracking via IActivityStore (as used by NavigationService).
/// Activity is tracked at the navigation level (ApplicationPage), not at the persistence layer.
/// </summary>
public class ActivityTrackingTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly InMemoryActivityStore _activityStore = new();
    private JsonSerializerOptions JsonOptions => Mesh.ServiceProvider.GetRequiredService<IMessageHub>().JsonSerializerOptions;

    private static UserActivityRecord CreateActivityRecord(string userId, string path, string? name = null, string? nodeType = null, ActivityType type = ActivityType.Read)
    {
        var now = System.DateTimeOffset.UtcNow;
        return new UserActivityRecord
        {
            Id = path.Replace("/", "_"),
            NodePath = path,
            UserId = userId,
            ActivityType = type,
            FirstAccessedAt = now,
            LastAccessedAt = now,
            AccessCount = 1,
            NodeName = name,
            NodeType = nodeType,
        };
    }

    [Fact(Timeout = 10000)]
    public async Task SaveActivities_TracksReadActivity()
    {
        // Arrange
        var userId = "user-123";
        var record = CreateActivityRecord(userId, "org/acme", "Acme Corp", "Type/Organization");

        // Act
        await _activityStore.SaveActivitiesAsync(userId, [record]);

        // Assert
        var activities = await _activityStore.GetActivitiesAsync(userId);
        activities.Should().ContainSingle();
        var activity = activities.First();
        activity.NodePath.Should().Be("org/acme");
        activity.UserId.Should().Be(userId);
        activity.ActivityType.Should().Be(ActivityType.Read);
        activity.AccessCount.Should().Be(1);
    }

    [Fact(Timeout = 10000)]
    public async Task SaveActivities_TracksWriteActivity()
    {
        // Arrange
        var userId = "user-456";
        var record = CreateActivityRecord(userId, "org/contoso", "Contoso Ltd", "Type/Organization", ActivityType.Write);

        // Act
        await _activityStore.SaveActivitiesAsync(userId, [record]);

        // Assert
        var activities = await _activityStore.GetActivitiesAsync(userId);
        activities.Should().ContainSingle();
        var activity = activities.First();
        activity.NodePath.Should().Be("org/contoso");
        activity.ActivityType.Should().Be(ActivityType.Write);
    }

    [Fact(Timeout = 10000)]
    public async Task MultipleAccesses_IncrementsAccessCount()
    {
        // Arrange
        var userId = "user-789";

        // Act - save activity for the same node multiple times (simulating repeated navigations)
        await _activityStore.SaveActivitiesAsync(userId, [CreateActivityRecord(userId, "org/fabrikam", "Fabrikam")]);
        await _activityStore.SaveActivitiesAsync(userId, [CreateActivityRecord(userId, "org/fabrikam", "Fabrikam")]);
        await _activityStore.SaveActivitiesAsync(userId, [CreateActivityRecord(userId, "org/fabrikam", "Fabrikam")]);

        // Assert
        var activities = await _activityStore.GetActivitiesAsync(userId);
        activities.Should().ContainSingle();
        activities.First().AccessCount.Should().Be(3);
    }

    [Fact(Timeout = 10000)]
    public async Task ActivityRecords_CanBeQueried()
    {
        // Arrange
        var userId = "user-query";

        // Save activities for multiple nodes
        await _activityStore.SaveActivitiesAsync(userId, [CreateActivityRecord(userId, "org/alpha", "Alpha", "Type/Organization")]);
        await _activityStore.SaveActivitiesAsync(userId, [CreateActivityRecord(userId, "org/beta", "Beta", "Type/Organization")]);
        await _activityStore.SaveActivitiesAsync(userId, [CreateActivityRecord(userId, "org/alpha", "Alpha", "Type/Organization")]); // Access alpha again

        // Act
        var activityRecords = (await _activityStore.GetActivitiesAsync(userId))
            .OrderByDescending(a => a.AccessCount)
            .ToList();

        // Assert
        activityRecords.Should().HaveCount(2);
        activityRecords.First().NodePath.Should().Be("org/alpha"); // More accesses
        activityRecords.First().AccessCount.Should().Be(2);
        activityRecords.Last().NodePath.Should().Be("org/beta");
        activityRecords.Last().AccessCount.Should().Be(1);
    }

    [Fact(Timeout = 10000)]
    public async Task ActivityRecords_OrderByLastAccessedAt_ReturnsCorrectOrder()
    {
        // Arrange
        var userId = "user-order";

        // Access in specific order with delays
        await _activityStore.SaveActivitiesAsync(userId, [CreateActivityRecord(userId, "org/first", "First", "Type/Organization")]);
        await Task.Delay(10);
        await _activityStore.SaveActivitiesAsync(userId, [CreateActivityRecord(userId, "org/second", "Second", "Type/Organization")]);
        await Task.Delay(10);
        await _activityStore.SaveActivitiesAsync(userId, [CreateActivityRecord(userId, "org/third", "Third", "Type/Organization")]);

        // Act
        var results = (await _activityStore.GetActivitiesAsync(userId)).ToList();

        // Assert
        results.Should().HaveCount(3);
        results[0].NodePath.Should().Be("org/third"); // Most recent
        results[1].NodePath.Should().Be("org/second");
        results[2].NodePath.Should().Be("org/first"); // Least recent
    }
}

public class CatalogFallbackTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private JsonSerializerOptions JsonOptions => Mesh.ServiceProvider.GetRequiredService<IMessageHub>().JsonSerializerOptions;

    [Fact(Timeout = 10000)]
    public async Task Catalog_NoActivity_FallsBackToActualNodes()
    {
        // Arrange - create persistence with nodes but no activity
        var persistence = new InMemoryPersistenceService();
        var meshQuery = new InMemoryMeshQuery(persistence);

        // Create some organization nodes
        await persistence.SaveNodeAsync(MeshNode.FromPath("org/acme") with
        {
            Name = "Acme",
            NodeType = "Type/Organization"
        }, JsonOptions);
        await persistence.SaveNodeAsync(MeshNode.FromPath("org/contoso") with
        {
            Name = "Contoso",
            NodeType = "Type/Organization"
        }, JsonOptions);

        // Act - query for organizations using standard query (simulating fallback)
        var query = "path:org nodeType:Type/Organization scope:descendants limit:20";
        var results = await meshQuery.QueryAsync(MeshQueryRequest.FromQuery(query), JsonOptions)
            .OfType<MeshNode>()
            .ToListAsync();

        // Assert - should return actual nodes when no activity
        results.Should().HaveCount(2);
        results.Select(n => n.Name).Should().Contain(["Acme", "Contoso"]);
    }

    [Fact(Timeout = 10000)]
    public async Task Catalog_WithActivity_CanLoadNodesFromActivityPaths()
    {
        // Arrange
        var persistence = new InMemoryPersistenceService();
        var activityStore = new InMemoryActivityStore();
        var userId = "catalog-user";

        // Create nodes
        await persistence.SaveNodeAsync(MeshNode.FromPath("org/alpha") with
        {
            Name = "Alpha",
            NodeType = "Type/Organization"
        }, JsonOptions);
        await persistence.SaveNodeAsync(MeshNode.FromPath("org/beta") with
        {
            Name = "Beta",
            NodeType = "Type/Organization"
        }, JsonOptions);
        await persistence.SaveNodeAsync(MeshNode.FromPath("org/gamma") with
        {
            Name = "Gamma",
            NodeType = "Type/Organization"
        }, JsonOptions);

        // Simulate navigation activity: beta first, then gamma, then alpha (alpha most recent)
        await activityStore.SaveActivitiesAsync(userId, [new UserActivityRecord
        {
            Id = "org_beta", NodePath = "org/beta", UserId = userId, ActivityType = ActivityType.Read,
            FirstAccessedAt = System.DateTimeOffset.UtcNow, LastAccessedAt = System.DateTimeOffset.UtcNow, AccessCount = 1, NodeName = "Beta", NodeType = "Type/Organization"
        }]);
        await Task.Delay(20);
        await activityStore.SaveActivitiesAsync(userId, [new UserActivityRecord
        {
            Id = "org_gamma", NodePath = "org/gamma", UserId = userId, ActivityType = ActivityType.Read,
            FirstAccessedAt = System.DateTimeOffset.UtcNow, LastAccessedAt = System.DateTimeOffset.UtcNow, AccessCount = 1, NodeName = "Gamma", NodeType = "Type/Organization"
        }]);
        await Task.Delay(20);
        await activityStore.SaveActivitiesAsync(userId, [new UserActivityRecord
        {
            Id = "org_alpha", NodePath = "org/alpha", UserId = userId, ActivityType = ActivityType.Read,
            FirstAccessedAt = System.DateTimeOffset.UtcNow, LastAccessedAt = System.DateTimeOffset.UtcNow, AccessCount = 1, NodeName = "Alpha", NodeType = "Type/Organization"
        }]);

        // Act - get activity records ordered by lastAccessedAt
        var activityRecords = (await activityStore.GetActivitiesAsync(userId)).ToList();

        // Load actual nodes from activity records (as Catalog does)
        var nodes = new List<MeshNode>();
        foreach (var activity in activityRecords)
        {
            var node = await persistence.GetNodeAsync(activity.NodePath, JsonOptions);
            if (node != null)
                nodes.Add(node);
        }

        // Assert - order should be based on activity (most recently accessed first)
        nodes.Should().HaveCount(3);
        nodes[0].Name.Should().Be("Alpha"); // Most recently accessed
        nodes[1].Name.Should().Be("Gamma");
        nodes[2].Name.Should().Be("Beta"); // First accessed
    }

    [Fact(Timeout = 10000)]
    public async Task Catalog_ActivityChangesOrder_AfterNewAccess()
    {
        // Arrange
        var activityStore = new InMemoryActivityStore();
        var userId = "reorder-user";

        // PHASE 1: Initial access order - doc1, doc2, doc3
        await activityStore.SaveActivitiesAsync(userId, [new UserActivityRecord
        {
            Id = "doc_doc1", NodePath = "doc/doc1", UserId = userId, ActivityType = ActivityType.Read,
            FirstAccessedAt = System.DateTimeOffset.UtcNow, LastAccessedAt = System.DateTimeOffset.UtcNow, AccessCount = 1, NodeName = "Document 1", NodeType = "Type/Document"
        }]);
        await Task.Delay(20);
        await activityStore.SaveActivitiesAsync(userId, [new UserActivityRecord
        {
            Id = "doc_doc2", NodePath = "doc/doc2", UserId = userId, ActivityType = ActivityType.Read,
            FirstAccessedAt = System.DateTimeOffset.UtcNow, LastAccessedAt = System.DateTimeOffset.UtcNow, AccessCount = 1, NodeName = "Document 2", NodeType = "Type/Document"
        }]);
        await Task.Delay(20);
        await activityStore.SaveActivitiesAsync(userId, [new UserActivityRecord
        {
            Id = "doc_doc3", NodePath = "doc/doc3", UserId = userId, ActivityType = ActivityType.Read,
            FirstAccessedAt = System.DateTimeOffset.UtcNow, LastAccessedAt = System.DateTimeOffset.UtcNow, AccessCount = 1, NodeName = "Document 3", NodeType = "Type/Document"
        }]);

        // Query initial order
        var initialOrder = (await activityStore.GetActivitiesAsync(userId)).Select(a => a.NodePath).ToList();
        initialOrder[0].Should().Be("doc/doc3"); // Most recent
        initialOrder[1].Should().Be("doc/doc2");
        initialOrder[2].Should().Be("doc/doc1"); // Least recent

        // PHASE 2: Access doc1 again - it should become most recent
        await Task.Delay(20);
        await activityStore.SaveActivitiesAsync(userId, [new UserActivityRecord
        {
            Id = "doc_doc1", NodePath = "doc/doc1", UserId = userId, ActivityType = ActivityType.Read,
            FirstAccessedAt = System.DateTimeOffset.UtcNow, LastAccessedAt = System.DateTimeOffset.UtcNow, AccessCount = 1, NodeName = "Document 1", NodeType = "Type/Document"
        }]);

        // Query new order
        var newOrder = (await activityStore.GetActivitiesAsync(userId)).Select(a => a.NodePath).ToList();
        newOrder[0].Should().Be("doc/doc1"); // Now most recent
        newOrder[1].Should().Be("doc/doc3");
        newOrder[2].Should().Be("doc/doc2");
    }
}

public class CatalogSearchAndPaginationTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private JsonSerializerOptions JsonOptions => Mesh.ServiceProvider.GetRequiredService<IMessageHub>().JsonSerializerOptions;

    [Fact(Timeout = 10000)]
    public async Task Catalog_SearchWithQuery_FiltersResults()
    {
        // Arrange
        var persistence = new InMemoryPersistenceService();
        var meshQuery = new InMemoryMeshQuery(persistence);

        // Create organizations with different names (use simple NodeType like existing tests)
        await persistence.SaveNodeAsync(MeshNode.FromPath("org/acme") with
        {
            Name = "Acme Corporation",
            NodeType = "Organization"
        }, JsonOptions);
        await persistence.SaveNodeAsync(MeshNode.FromPath("org/contoso") with
        {
            Name = "Contoso Ltd",
            NodeType = "Organization"
        }, JsonOptions);
        await persistence.SaveNodeAsync(MeshNode.FromPath("org/fabrikam") with
        {
            Name = "Fabrikam Inc",
            NodeType = "Organization"
        }, JsonOptions);

        // Act - query with filter for name containing "Corp" using wildcard operator
        var query = "path:org nodeType:Organization name:*Corp* scope:descendants limit:20";
        var results = await meshQuery.QueryAsync(MeshQueryRequest.FromQuery(query), JsonOptions)
            .OfType<MeshNode>()
            .ToListAsync();

        // Assert - should only return Acme Corporation
        results.Should().ContainSingle();
        results.First().Name.Should().Be("Acme Corporation");
    }

    [Fact(Timeout = 10000)]
    public async Task Catalog_TextSearch_FiltersResults()
    {
        // Arrange
        var persistence = new InMemoryPersistenceService();
        var meshQuery = new InMemoryMeshQuery(persistence);

        await persistence.SaveNodeAsync(MeshNode.FromPath("doc/report1") with
        {
            Name = "Annual Financial Report 2024",
            NodeType = "Document"
        }, JsonOptions);
        await persistence.SaveNodeAsync(MeshNode.FromPath("doc/memo1") with
        {
            Name = "Team Meeting Notes",
            NodeType = "Document"
        }, JsonOptions);

        // Act - text search for "financial" with descendants scope
        var query = "path:doc nodeType:Document financial scope:descendants limit:20";
        var results = await meshQuery.QueryAsync(MeshQueryRequest.FromQuery(query), JsonOptions)
            .OfType<MeshNode>()
            .ToListAsync();

        // Assert
        results.Should().ContainSingle();
        results.First().Name.Should().Be("Annual Financial Report 2024");
    }

    [Fact(Timeout = 10000)]
    public async Task Catalog_Pagination_LoadsMoreItems()
    {
        // Arrange
        var persistence = new InMemoryPersistenceService();
        var meshQuery = new InMemoryMeshQuery(persistence);

        // Create 10 items
        for (int i = 0; i < 10; i++)
        {
            await persistence.SaveNodeAsync(MeshNode.FromPath($"item/item{i:D2}") with
            {
                Name = $"Item {i:D2}",
                NodeType = "Item"
            }, JsonOptions);
        }

        // Act - first page (3 items) with descendants scope
        var firstPage = await meshQuery.QueryAsync(MeshQueryRequest.FromQuery("path:item nodeType:Item scope:descendants limit:3"), JsonOptions)
            .OfType<MeshNode>()
            .ToListAsync();

        // Load more (6 items total)
        var secondPage = await meshQuery.QueryAsync(MeshQueryRequest.FromQuery("path:item nodeType:Item scope:descendants limit:6"), JsonOptions)
            .OfType<MeshNode>()
            .ToListAsync();

        // Load all (10 items)
        var allItems = await meshQuery.QueryAsync(MeshQueryRequest.FromQuery("path:item nodeType:Item scope:descendants limit:100"), JsonOptions)
            .OfType<MeshNode>()
            .ToListAsync();

        // Assert
        firstPage.Should().HaveCount(3);
        secondPage.Should().HaveCount(6);
        allItems.Should().HaveCount(10);
    }

    [Fact(Timeout = 10000)]
    public async Task Catalog_HasMore_DetectedCorrectly()
    {
        // Arrange
        var persistence = new InMemoryPersistenceService();
        var meshQuery = new InMemoryMeshQuery(persistence);

        // Create 5 items
        for (int i = 0; i < 5; i++)
        {
            await persistence.SaveNodeAsync(MeshNode.FromPath($"test/node{i}") with
            {
                Name = $"Node {i}",
                NodeType = "Test"
            }, JsonOptions);
        }

        // Act - request limit+1 to detect if there are more
        var limit = 3;
        var queryLimit = limit + 1;
        var results = await meshQuery.QueryAsync(MeshQueryRequest.FromQuery($"path:test nodeType:Test scope:descendants limit:{queryLimit}"), JsonOptions)
            .OfType<MeshNode>()
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
    public async Task Catalog_ActivityRecords_CanBeFilteredByNodeType()
    {
        // Arrange
        var persistence = new InMemoryPersistenceService();
        var activityStore = new InMemoryActivityStore();
        var userId = "search-user";

        // Create nodes with different types
        await persistence.SaveNodeAsync(MeshNode.FromPath("data/project1") with
        {
            Name = "Project Alpha",
            NodeType = "Project"
        }, JsonOptions);
        await persistence.SaveNodeAsync(MeshNode.FromPath("data/doc1") with
        {
            Name = "Document One",
            NodeType = "Document"
        }, JsonOptions);
        await persistence.SaveNodeAsync(MeshNode.FromPath("data/project2") with
        {
            Name = "Project Beta",
            NodeType = "Project"
        }, JsonOptions);

        // Simulate navigation activity for all nodes
        await activityStore.SaveActivitiesAsync(userId, [new UserActivityRecord
        {
            Id = "data_project1", NodePath = "data/project1", UserId = userId, ActivityType = ActivityType.Read,
            FirstAccessedAt = System.DateTimeOffset.UtcNow, LastAccessedAt = System.DateTimeOffset.UtcNow, AccessCount = 1, NodeName = "Project Alpha", NodeType = "Project"
        }]);
        await Task.Delay(20);
        await activityStore.SaveActivitiesAsync(userId, [new UserActivityRecord
        {
            Id = "data_doc1", NodePath = "data/doc1", UserId = userId, ActivityType = ActivityType.Read,
            FirstAccessedAt = System.DateTimeOffset.UtcNow, LastAccessedAt = System.DateTimeOffset.UtcNow, AccessCount = 1, NodeName = "Document One", NodeType = "Document"
        }]);
        await Task.Delay(20);
        await activityStore.SaveActivitiesAsync(userId, [new UserActivityRecord
        {
            Id = "data_project2", NodePath = "data/project2", UserId = userId, ActivityType = ActivityType.Read,
            FirstAccessedAt = System.DateTimeOffset.UtcNow, LastAccessedAt = System.DateTimeOffset.UtcNow, AccessCount = 1, NodeName = "Project Beta", NodeType = "Project"
        }]);

        // Act - get activity records and filter by nodeType manually (as catalog would do)
        var allActivityRecords = (await activityStore.GetActivitiesAsync(userId)).ToList();

        // Load nodes and filter by type
        var projectRecords = new List<UserActivityRecord>();
        foreach (var activity in allActivityRecords)
        {
            var node = await persistence.GetNodeAsync(activity.NodePath, JsonOptions);
            if (node?.NodeType == "Project")
            {
                projectRecords.Add(activity);
            }
        }

        // Assert - should return only Project nodes, not the Document
        projectRecords.Should().HaveCount(2);
        projectRecords.Select(a => a.NodePath).Should().Contain(["data/project1", "data/project2"]);
        projectRecords.Select(a => a.NodePath).Should().NotContain("data/doc1");
        // Most recent first
        projectRecords[0].NodePath.Should().Be("data/project2");
    }
}

/// <summary>
/// Tests that source:activity queries work via InMemoryMeshQuery.
/// InMemory doesn't support SQL JOIN, so source:activity is treated like a normal query
/// (returns all matching nodes without activity ordering). Activity ordering is only
/// available with PostgreSQL provider.
/// </summary>
public class InMemoryActivityOrderedQueryTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private JsonSerializerOptions JsonOptions => Mesh.ServiceProvider.GetRequiredService<IMessageHub>().JsonSerializerOptions;

    [Fact(Timeout = 10000)]
    public async Task SourceActivity_ReturnsAllMatchingNodes()
    {
        // Arrange
        var persistence = new InMemoryPersistenceService();
        var meshQuery = new InMemoryMeshQuery(persistence);

        await persistence.SaveNodeAsync(MeshNode.FromPath("org/alpha") with
        {
            Name = "Alpha",
            NodeType = "Organization"
        }, JsonOptions);
        await persistence.SaveNodeAsync(MeshNode.FromPath("org/beta") with
        {
            Name = "Beta",
            NodeType = "Organization"
        }, JsonOptions);
        await persistence.SaveNodeAsync(MeshNode.FromPath("org/gamma") with
        {
            Name = "Gamma",
            NodeType = "Organization"
        }, JsonOptions);

        // Act - source:activity query via InMemory (filters still apply, ordering is default)
        var query = "source:activity nodeType:Organization path:org scope:children";
        var results = await meshQuery.QueryAsync(MeshQueryRequest.FromQuery(query), JsonOptions)
            .OfType<MeshNode>()
            .ToListAsync();

        // Assert - all matching nodes are returned (source:activity is stripped by parser)
        results.Should().HaveCount(3);
        results.Select(n => n.Name).Should().Contain(["Alpha", "Beta", "Gamma"]);
    }

    [Fact(Timeout = 10000)]
    public async Task SourceActivity_RespectsNodeTypeFilter()
    {
        // Arrange
        var persistence = new InMemoryPersistenceService();
        var meshQuery = new InMemoryMeshQuery(persistence);

        await persistence.SaveNodeAsync(MeshNode.FromPath("data/proj1") with
        {
            Name = "Project One",
            NodeType = "Project"
        }, JsonOptions);
        await persistence.SaveNodeAsync(MeshNode.FromPath("data/doc1") with
        {
            Name = "Document One",
            NodeType = "Document"
        }, JsonOptions);

        // Act
        var query = "source:activity nodeType:Project path:data scope:children";
        var results = await meshQuery.QueryAsync(MeshQueryRequest.FromQuery(query), JsonOptions)
            .OfType<MeshNode>()
            .ToListAsync();

        // Assert - only Project nodes returned
        results.Should().ContainSingle();
        results[0].Name.Should().Be("Project One");
    }

    [Fact(Timeout = 10000)]
    public async Task SourceActivity_RespectsLimit()
    {
        // Arrange
        var persistence = new InMemoryPersistenceService();
        var meshQuery = new InMemoryMeshQuery(persistence);

        for (var i = 0; i < 5; i++)
        {
            await persistence.SaveNodeAsync(MeshNode.FromPath($"items/item{i}") with
            {
                Name = $"Item {i}",
                NodeType = "Item"
            }, JsonOptions);
        }

        // Act
        var query = "source:activity nodeType:Item path:items scope:children limit:3";
        var results = await meshQuery.QueryAsync(MeshQueryRequest.FromQuery(query), JsonOptions)
            .OfType<MeshNode>()
            .ToListAsync();

        // Assert
        results.Should().HaveCount(3);
    }
}
