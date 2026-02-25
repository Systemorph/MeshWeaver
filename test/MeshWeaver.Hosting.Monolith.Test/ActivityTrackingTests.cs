using System;
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
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

public class ActivityTrackingTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly InMemoryPersistenceService _innerPersistence = new();
    private readonly InMemoryActivityStore _activityStore = new();
    private readonly AccessService _accessService = new();
    private ActivityTrackingPersistenceDecorator? _decoratorInstance;
    private ActivityTrackingPersistenceDecorator _decorator => _decoratorInstance ??= new(
        _innerPersistence,
        _activityStore,
        _accessService,
        NullLogger<ActivityTrackingPersistenceDecorator>.Instance);
    private JsonSerializerOptions JsonOptions => Mesh.ServiceProvider.GetRequiredService<IMessageHub>().JsonSerializerOptions;

    [Fact(Timeout = 10000)]
    public async Task GetNodeAsync_TracksReadActivity()
    {
        // Arrange
        var userId = "user-123";
        _accessService.SetContext(new AccessContext { ObjectId = userId });
        await _innerPersistence.SaveNodeAsync(MeshNode.FromPath("org/acme") with
        {
            Name = "Acme Corp",
            NodeType = "Type/Organization"
        }, JsonOptions);

        // Act
        var node = await _decorator.GetNodeAsync("org/acme", JsonOptions);
        await _decorator.FlushPendingActivitiesAsync();

        // Assert
        node.Should().NotBeNull();

        // Check activity was recorded
        var activities = await _activityStore.GetActivitiesAsync(userId);

        activities.Should().ContainSingle();
        var activity = activities.First();
        activity.NodePath.Should().Be("org/acme");
        activity.UserId.Should().Be(userId);
        activity.ActivityType.Should().Be(ActivityType.Read);
        activity.AccessCount.Should().Be(1);
    }

    [Fact(Timeout = 10000)]
    public async Task SaveNodeAsync_TracksWriteActivity()
    {
        // Arrange
        var userId = "user-456";
        _accessService.SetContext(new AccessContext { ObjectId = userId });

        // Act
        await _decorator.SaveNodeAsync(MeshNode.FromPath("org/contoso") with
        {
            Name = "Contoso Ltd",
            NodeType = "Type/Organization"
        }, JsonOptions);

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
        _accessService.SetContext(new AccessContext { ObjectId = userId });
        await _innerPersistence.SaveNodeAsync(MeshNode.FromPath("org/fabrikam") with { Name = "Fabrikam" }, JsonOptions);

        // Act - access the same node multiple times
        await _decorator.GetNodeAsync("org/fabrikam", JsonOptions);
        await _decorator.GetNodeAsync("org/fabrikam", JsonOptions);
        await _decorator.GetNodeAsync("org/fabrikam", JsonOptions);
        await _decorator.FlushPendingActivitiesAsync();

        // Assert
        var activities = await _activityStore.GetActivitiesAsync(userId);

        activities.Should().ContainSingle();
        activities.First().AccessCount.Should().Be(3);
    }

    [Fact(Timeout = 10000)]
    public async Task NoUserContext_DoesNotTrackActivity()
    {
        // Arrange - no user context set
        _accessService.SetContext(null);
        await _innerPersistence.SaveNodeAsync(MeshNode.FromPath("org/anonymous") with { Name = "Anonymous" }, JsonOptions);

        // Act
        await _decorator.GetNodeAsync("org/anonymous", JsonOptions);
        await _decorator.FlushPendingActivitiesAsync();

        // Assert - no activity should exist (no user context)
        var activities = await _activityStore.GetActivitiesAsync("");
        activities.Should().BeEmpty();
    }

    [Fact(Timeout = 10000)]
    public async Task ActivityPaths_NotTracked()
    {
        // Arrange
        var userId = "user-activity";
        _accessService.SetContext(new AccessContext { ObjectId = userId });
        await _innerPersistence.SaveNodeAsync(MeshNode.FromPath("_activity/test") with { Name = "Activity Node" }, JsonOptions);

        // Act
        await _decorator.GetNodeAsync("_activity/test", JsonOptions);
        await _decorator.FlushPendingActivitiesAsync();

        // Assert - should not track access to _activity paths (system paths skipped)
        var activities = await _activityStore.GetActivitiesAsync(userId);
        activities.Should().BeEmpty();
    }

    [Fact(Timeout = 10000)]
    public async Task ActivityRecords_CanBeQueried_FromPartition()
    {
        // Arrange
        var userId = "user-query";
        _accessService.SetContext(new AccessContext { ObjectId = userId });

        // Create some nodes
        await _innerPersistence.SaveNodeAsync(MeshNode.FromPath("org/alpha") with
        {
            Name = "Alpha",
            NodeType = "Type/Organization"
        }, JsonOptions);
        await _innerPersistence.SaveNodeAsync(MeshNode.FromPath("org/beta") with
        {
            Name = "Beta",
            NodeType = "Type/Organization"
        }, JsonOptions);

        // Access nodes to create activity
        await _decorator.GetNodeAsync("org/alpha", JsonOptions);
        await _decorator.GetNodeAsync("org/beta", JsonOptions);
        await _decorator.GetNodeAsync("org/alpha", JsonOptions); // Access alpha again
        await _decorator.FlushPendingActivitiesAsync();

        // Act - query user's activity
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
        _accessService.SetContext(new AccessContext { ObjectId = userId });

        // Create nodes
        await _innerPersistence.SaveNodeAsync(MeshNode.FromPath("org/first") with
        {
            Name = "First",
            NodeType = "Type/Organization"
        }, JsonOptions);
        await _innerPersistence.SaveNodeAsync(MeshNode.FromPath("org/second") with
        {
            Name = "Second",
            NodeType = "Type/Organization"
        }, JsonOptions);
        await _innerPersistence.SaveNodeAsync(MeshNode.FromPath("org/third") with
        {
            Name = "Third",
            NodeType = "Type/Organization"
        }, JsonOptions);

        // Access in specific order
        await _decorator.GetNodeAsync("org/first", JsonOptions);
        await Task.Delay(10); // Small delay to ensure different timestamps
        await _decorator.GetNodeAsync("org/second", JsonOptions);
        await Task.Delay(10);
        await _decorator.GetNodeAsync("org/third", JsonOptions);
        await _decorator.FlushPendingActivitiesAsync();

        // Act - get activity records ordered by lastAccessedAt descending (most recent first)
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
        var innerPersistence = new InMemoryPersistenceService();
        var accessService = new AccessService();
        accessService.SetContext(new AccessContext { ObjectId = "catalog-user" });

        var activityStore = new InMemoryActivityStore();
        var decorator = new ActivityTrackingPersistenceDecorator(
            innerPersistence,
            activityStore,
            accessService,
            NullLogger<ActivityTrackingPersistenceDecorator>.Instance);

        // Create nodes
        await innerPersistence.SaveNodeAsync(MeshNode.FromPath("org/alpha") with
        {
            Name = "Alpha",
            NodeType = "Type/Organization"
        }, JsonOptions);
        await innerPersistence.SaveNodeAsync(MeshNode.FromPath("org/beta") with
        {
            Name = "Beta",
            NodeType = "Type/Organization"
        }, JsonOptions);
        await innerPersistence.SaveNodeAsync(MeshNode.FromPath("org/gamma") with
        {
            Name = "Gamma",
            NodeType = "Type/Organization"
        }, JsonOptions);

        // Access beta first, then gamma, then alpha (so alpha is most recent)
        await decorator.GetNodeAsync("org/beta", JsonOptions);
        await Task.Delay(10);
        await decorator.GetNodeAsync("org/gamma", JsonOptions);
        await Task.Delay(10);
        await decorator.GetNodeAsync("org/alpha", JsonOptions);
        await decorator.FlushPendingActivitiesAsync();

        // Act - get activity records ordered by lastAccessedAt
        var activityRecords = (await activityStore.GetActivitiesAsync("catalog-user")).ToList();

        // Load actual nodes from activity records (as Catalog does)
        var nodes = new List<MeshNode>();
        foreach (var activity in activityRecords)
        {
            var node = await innerPersistence.GetNodeAsync(activity.NodePath, JsonOptions);
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
        var innerPersistence = new InMemoryPersistenceService();
        var accessService = new AccessService();
        accessService.SetContext(new AccessContext { ObjectId = "reorder-user" });

        // Create nodes
        await innerPersistence.SaveNodeAsync(MeshNode.FromPath("doc/doc1") with
        {
            Name = "Document 1",
            NodeType = "Type/Document"
        }, JsonOptions);
        await innerPersistence.SaveNodeAsync(MeshNode.FromPath("doc/doc2") with
        {
            Name = "Document 2",
            NodeType = "Type/Document"
        }, JsonOptions);
        await innerPersistence.SaveNodeAsync(MeshNode.FromPath("doc/doc3") with
        {
            Name = "Document 3",
            NodeType = "Type/Document"
        }, JsonOptions);

        // PHASE 1: Initial access order - doc1, doc2, doc3
        var activityStore = new InMemoryActivityStore();
        var decorator1 = new ActivityTrackingPersistenceDecorator(
            innerPersistence, activityStore, accessService, NullLogger<ActivityTrackingPersistenceDecorator>.Instance);
        await decorator1.GetNodeAsync("doc/doc1", JsonOptions);
        await Task.Delay(10);
        await decorator1.GetNodeAsync("doc/doc2", JsonOptions);
        await Task.Delay(10);
        await decorator1.GetNodeAsync("doc/doc3", JsonOptions);
        await decorator1.FlushPendingActivitiesAsync();

        // Query initial order
        var initialOrder = (await activityStore.GetActivitiesAsync("reorder-user")).Select(a => a.NodePath).ToList();
        initialOrder[0].Should().Be("doc/doc3"); // Most recent
        initialOrder[1].Should().Be("doc/doc2");
        initialOrder[2].Should().Be("doc/doc1"); // Least recent

        // PHASE 2: Access doc1 again - it should become most recent
        var decorator2 = new ActivityTrackingPersistenceDecorator(
            innerPersistence, activityStore, accessService, NullLogger<ActivityTrackingPersistenceDecorator>.Instance);
        await Task.Delay(20);
        await decorator2.GetNodeAsync("doc/doc1", JsonOptions);
        await decorator2.FlushPendingActivitiesAsync();

        // Query new order
        var newOrder = (await activityStore.GetActivitiesAsync("reorder-user")).Select(a => a.NodePath).ToList();
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
        var innerPersistence = new InMemoryPersistenceService();
        var accessService = new AccessService();
        accessService.SetContext(new AccessContext { ObjectId = "search-user" });

        // Create nodes with different types
        await innerPersistence.SaveNodeAsync(MeshNode.FromPath("data/project1") with
        {
            Name = "Project Alpha",
            NodeType = "Project"
        }, JsonOptions);
        await innerPersistence.SaveNodeAsync(MeshNode.FromPath("data/doc1") with
        {
            Name = "Document One",
            NodeType = "Document"
        }, JsonOptions);
        await innerPersistence.SaveNodeAsync(MeshNode.FromPath("data/project2") with
        {
            Name = "Project Beta",
            NodeType = "Project"
        }, JsonOptions);

        // Access all nodes to create activity
        var activityStore = new InMemoryActivityStore();
        var decorator = new ActivityTrackingPersistenceDecorator(
            innerPersistence, activityStore, accessService, NullLogger<ActivityTrackingPersistenceDecorator>.Instance);
        await decorator.GetNodeAsync("data/project1", JsonOptions);
        await Task.Delay(10);
        await decorator.GetNodeAsync("data/doc1", JsonOptions);
        await Task.Delay(10);
        await decorator.GetNodeAsync("data/project2", JsonOptions);
        await decorator.FlushPendingActivitiesAsync();

        // Act - get activity records and filter by nodeType manually (as catalog would do)
        var allActivityRecords = (await activityStore.GetActivitiesAsync("search-user")).ToList();

        // Load nodes and filter by type
        var projectRecords = new List<UserActivityRecord>();
        foreach (var activity in allActivityRecords)
        {
            var node = await innerPersistence.GetNodeAsync(activity.NodePath, JsonOptions);
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
