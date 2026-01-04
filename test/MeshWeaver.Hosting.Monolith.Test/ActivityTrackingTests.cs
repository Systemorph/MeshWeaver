using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Hosting.Activity;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Activity;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

public class ActivityTrackingTests : IDisposable
{
    private readonly InMemoryPersistenceService _innerPersistence;
    private readonly AccessService _accessService;
    private readonly ActivityTrackingPersistenceDecorator _decorator;

    public ActivityTrackingTests()
    {
        _innerPersistence = new InMemoryPersistenceService();
        _accessService = new AccessService();
        _decorator = new ActivityTrackingPersistenceDecorator(
            _innerPersistence,
            _accessService,
            NullLogger<ActivityTrackingPersistenceDecorator>.Instance);
    }

    public void Dispose()
    {
        _decorator.Dispose();
    }

    [Fact]
    public async Task GetNodeAsync_TracksReadActivity()
    {
        // Arrange
        var userId = "user-123";
        _accessService.SetContext(new AccessContext { ObjectId = userId });
        await _innerPersistence.SaveNodeAsync(MeshNode.FromPath("org/acme") with
        {
            Name = "Acme Corp",
            NodeType = "Type/Organization"
        });

        // Act
        var node = await _decorator.GetNodeAsync("org/acme");

        // Assert
        node.Should().NotBeNull();

        // Flush pending activities
        _decorator.Dispose();

        // Check activity was recorded
        var activities = await _innerPersistence.GetPartitionObjectsAsync($"_activity/{userId}")
            .OfType<UserActivityRecord>()
            .ToListAsync();

        activities.Should().ContainSingle();
        var activity = activities.First();
        activity.NodePath.Should().Be("org/acme");
        activity.UserId.Should().Be(userId);
        activity.ActivityType.Should().Be(ActivityType.Read);
        activity.AccessCount.Should().Be(1);
    }

    [Fact]
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
        });

        // Flush pending activities
        _decorator.Dispose();

        // Assert
        var activities = await _innerPersistence.GetPartitionObjectsAsync($"_activity/{userId}")
            .OfType<UserActivityRecord>()
            .ToListAsync();

        activities.Should().ContainSingle();
        var activity = activities.First();
        activity.NodePath.Should().Be("org/contoso");
        activity.ActivityType.Should().Be(ActivityType.Write);
    }

    [Fact]
    public async Task MultipleAccesses_IncrementsAccessCount()
    {
        // Arrange
        var userId = "user-789";
        _accessService.SetContext(new AccessContext { ObjectId = userId });
        await _innerPersistence.SaveNodeAsync(MeshNode.FromPath("org/fabrikam") with { Name = "Fabrikam" });

        // Act - access the same node multiple times
        await _decorator.GetNodeAsync("org/fabrikam");
        await _decorator.GetNodeAsync("org/fabrikam");
        await _decorator.GetNodeAsync("org/fabrikam");

        // Flush pending activities
        _decorator.Dispose();

        // Assert
        var activities = await _innerPersistence.GetPartitionObjectsAsync($"_activity/{userId}")
            .OfType<UserActivityRecord>()
            .ToListAsync();

        activities.Should().ContainSingle();
        activities.First().AccessCount.Should().Be(3);
    }

    [Fact]
    public async Task NoUserContext_DoesNotTrackActivity()
    {
        // Arrange - no user context set
        _accessService.SetContext(null);
        await _innerPersistence.SaveNodeAsync(MeshNode.FromPath("org/anonymous") with { Name = "Anonymous" });

        // Act
        await _decorator.GetNodeAsync("org/anonymous");

        // Flush
        _decorator.Dispose();

        // Assert - no activity partition should exist
        var activities = await _innerPersistence.GetPartitionObjectsAsync("_activity/")
            .ToListAsync();
        activities.Should().BeEmpty();
    }

    [Fact]
    public async Task ActivityPaths_NotTracked()
    {
        // Arrange
        var userId = "user-activity";
        _accessService.SetContext(new AccessContext { ObjectId = userId });
        await _innerPersistence.SaveNodeAsync(MeshNode.FromPath("_activity/test") with { Name = "Activity Node" });

        // Act
        await _decorator.GetNodeAsync("_activity/test");

        // Flush
        _decorator.Dispose();

        // Assert - should not track access to _activity paths (avoids infinite loop)
        var activities = await _innerPersistence.GetPartitionObjectsAsync($"_activity/{userId}")
            .OfType<UserActivityRecord>()
            .ToListAsync();
        activities.Should().BeEmpty();
    }

    [Fact]
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
        });
        await _innerPersistence.SaveNodeAsync(MeshNode.FromPath("org/beta") with
        {
            Name = "Beta",
            NodeType = "Type/Organization"
        });

        // Access nodes to create activity
        await _decorator.GetNodeAsync("org/alpha");
        await _decorator.GetNodeAsync("org/beta");
        await _decorator.GetNodeAsync("org/alpha"); // Access alpha again

        // Flush pending activities
        _decorator.Dispose();

        // Act - query user's activity directly from partition
        var activityRecords = await _innerPersistence.GetPartitionObjectsAsync($"_activity/{userId}")
            .OfType<UserActivityRecord>()
            .OrderByDescending(a => a.AccessCount)
            .ToListAsync();

        // Assert
        activityRecords.Should().HaveCount(2);
        activityRecords.First().NodePath.Should().Be("org/alpha"); // More accesses
        activityRecords.First().AccessCount.Should().Be(2);
        activityRecords.Last().NodePath.Should().Be("org/beta");
        activityRecords.Last().AccessCount.Should().Be(1);
    }

    [Fact]
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
        });
        await _innerPersistence.SaveNodeAsync(MeshNode.FromPath("org/second") with
        {
            Name = "Second",
            NodeType = "Type/Organization"
        });
        await _innerPersistence.SaveNodeAsync(MeshNode.FromPath("org/third") with
        {
            Name = "Third",
            NodeType = "Type/Organization"
        });

        // Access in specific order
        await _decorator.GetNodeAsync("org/first");
        await Task.Delay(10); // Small delay to ensure different timestamps
        await _decorator.GetNodeAsync("org/second");
        await Task.Delay(10);
        await _decorator.GetNodeAsync("org/third");

        // Flush pending activities
        _decorator.Dispose();

        // Act - get activity records ordered by lastAccessedAt descending (most recent first)
        var results = await _innerPersistence.GetPartitionObjectsAsync($"_activity/{userId}")
            .OfType<UserActivityRecord>()
            .OrderByDescending(a => a.LastAccessedAt)
            .ToListAsync();

        // Assert
        results.Should().HaveCount(3);
        results[0].NodePath.Should().Be("org/third"); // Most recent
        results[1].NodePath.Should().Be("org/second");
        results[2].NodePath.Should().Be("org/first"); // Least recent
    }
}

public class CatalogFallbackTests
{
    [Fact]
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
        });
        await persistence.SaveNodeAsync(MeshNode.FromPath("org/contoso") with
        {
            Name = "Contoso",
            NodeType = "Type/Organization"
        });

        // Act - query for organizations using standard query (simulating fallback)
        var query = "path:org nodeType:Type/Organization scope:descendants limit:20";
        var results = await meshQuery.QueryAsync(MeshQueryRequest.FromQuery(query))
            .OfType<MeshNode>()
            .ToListAsync();

        // Assert - should return actual nodes when no activity
        results.Should().HaveCount(2);
        results.Select(n => n.Name).Should().Contain(["Acme", "Contoso"]);
    }

    [Fact]
    public async Task Catalog_WithActivity_CanLoadNodesFromActivityPaths()
    {
        // Arrange
        var innerPersistence = new InMemoryPersistenceService();
        var accessService = new AccessService();
        accessService.SetContext(new AccessContext { ObjectId = "catalog-user" });

        using var decorator = new ActivityTrackingPersistenceDecorator(
            innerPersistence,
            accessService,
            NullLogger<ActivityTrackingPersistenceDecorator>.Instance);

        // Create nodes
        await innerPersistence.SaveNodeAsync(MeshNode.FromPath("org/alpha") with
        {
            Name = "Alpha",
            NodeType = "Type/Organization"
        });
        await innerPersistence.SaveNodeAsync(MeshNode.FromPath("org/beta") with
        {
            Name = "Beta",
            NodeType = "Type/Organization"
        });
        await innerPersistence.SaveNodeAsync(MeshNode.FromPath("org/gamma") with
        {
            Name = "Gamma",
            NodeType = "Type/Organization"
        });

        // Access beta first, then gamma, then alpha (so alpha is most recent)
        await decorator.GetNodeAsync("org/beta");
        await Task.Delay(10);
        await decorator.GetNodeAsync("org/gamma");
        await Task.Delay(10);
        await decorator.GetNodeAsync("org/alpha");

        // Flush activities
        decorator.Dispose();

        // Act - get activity records ordered by lastAccessedAt
        var activityRecords = await innerPersistence.GetPartitionObjectsAsync("_activity/catalog-user")
            .OfType<UserActivityRecord>()
            .OrderByDescending(a => a.LastAccessedAt)
            .ToListAsync();

        // Load actual nodes from activity records (as Catalog does)
        var nodes = new List<MeshNode>();
        foreach (var activity in activityRecords)
        {
            var node = await innerPersistence.GetNodeAsync(activity.NodePath);
            if (node != null)
                nodes.Add(node);
        }

        // Assert - order should be based on activity (most recently accessed first)
        nodes.Should().HaveCount(3);
        nodes[0].Name.Should().Be("Alpha"); // Most recently accessed
        nodes[1].Name.Should().Be("Gamma");
        nodes[2].Name.Should().Be("Beta"); // First accessed
    }

    [Fact]
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
        });
        await innerPersistence.SaveNodeAsync(MeshNode.FromPath("doc/doc2") with
        {
            Name = "Document 2",
            NodeType = "Type/Document"
        });
        await innerPersistence.SaveNodeAsync(MeshNode.FromPath("doc/doc3") with
        {
            Name = "Document 3",
            NodeType = "Type/Document"
        });

        // PHASE 1: Initial access order - doc1, doc2, doc3
        using (var decorator1 = new ActivityTrackingPersistenceDecorator(
            innerPersistence, accessService, NullLogger<ActivityTrackingPersistenceDecorator>.Instance))
        {
            await decorator1.GetNodeAsync("doc/doc1");
            await Task.Delay(10);
            await decorator1.GetNodeAsync("doc/doc2");
            await Task.Delay(10);
            await decorator1.GetNodeAsync("doc/doc3");
        } // Dispose flushes

        // Query initial order
        var initialOrder = await GetActivityOrder(innerPersistence, "reorder-user");
        initialOrder[0].Should().Be("doc/doc3"); // Most recent
        initialOrder[1].Should().Be("doc/doc2");
        initialOrder[2].Should().Be("doc/doc1"); // Least recent

        // PHASE 2: Access doc1 again - it should become most recent
        using (var decorator2 = new ActivityTrackingPersistenceDecorator(
            innerPersistence, accessService, NullLogger<ActivityTrackingPersistenceDecorator>.Instance))
        {
            await Task.Delay(20);
            await decorator2.GetNodeAsync("doc/doc1");
        } // Dispose flushes

        // Query new order
        var newOrder = await GetActivityOrder(innerPersistence, "reorder-user");
        newOrder[0].Should().Be("doc/doc1"); // Now most recent
        newOrder[1].Should().Be("doc/doc3");
        newOrder[2].Should().Be("doc/doc2");
    }

    private async Task<List<string>> GetActivityOrder(InMemoryPersistenceService persistence, string userId)
    {
        return await persistence.GetPartitionObjectsAsync($"_activity/{userId}")
            .OfType<UserActivityRecord>()
            .OrderByDescending(a => a.LastAccessedAt)
            .Select(a => a.NodePath)
            .ToListAsync();
    }
}

public class CatalogSearchAndPaginationTests
{
    [Fact]
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
        });
        await persistence.SaveNodeAsync(MeshNode.FromPath("org/contoso") with
        {
            Name = "Contoso Ltd",
            NodeType = "Organization"
        });
        await persistence.SaveNodeAsync(MeshNode.FromPath("org/fabrikam") with
        {
            Name = "Fabrikam Inc",
            NodeType = "Organization"
        });

        // Act - query with filter for name containing "Corp" using wildcard operator
        var query = "path:org nodeType:Organization name:*Corp* scope:descendants limit:20";
        var results = await meshQuery.QueryAsync(MeshQueryRequest.FromQuery(query))
            .OfType<MeshNode>()
            .ToListAsync();

        // Assert - should only return Acme Corporation
        results.Should().ContainSingle();
        results.First().Name.Should().Be("Acme Corporation");
    }

    [Fact]
    public async Task Catalog_TextSearch_FiltersResults()
    {
        // Arrange
        var persistence = new InMemoryPersistenceService();
        var meshQuery = new InMemoryMeshQuery(persistence);

        await persistence.SaveNodeAsync(MeshNode.FromPath("doc/report1") with
        {
            Name = "Annual Report 2024",
            Description = "Financial summary for fiscal year",
            NodeType = "Document"
        });
        await persistence.SaveNodeAsync(MeshNode.FromPath("doc/memo1") with
        {
            Name = "Team Meeting Notes",
            Description = "Weekly sync discussion points",
            NodeType = "Document"
        });

        // Act - text search for "financial" with descendants scope
        var query = "path:doc nodeType:Document financial scope:descendants limit:20";
        var results = await meshQuery.QueryAsync(MeshQueryRequest.FromQuery(query))
            .OfType<MeshNode>()
            .ToListAsync();

        // Assert
        results.Should().ContainSingle();
        results.First().Name.Should().Be("Annual Report 2024");
    }

    [Fact]
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
            });
        }

        // Act - first page (3 items) with descendants scope
        var firstPage = await meshQuery.QueryAsync(MeshQueryRequest.FromQuery("path:item nodeType:Item scope:descendants limit:3"))
            .OfType<MeshNode>()
            .ToListAsync();

        // Load more (6 items total)
        var secondPage = await meshQuery.QueryAsync(MeshQueryRequest.FromQuery("path:item nodeType:Item scope:descendants limit:6"))
            .OfType<MeshNode>()
            .ToListAsync();

        // Load all (10 items)
        var allItems = await meshQuery.QueryAsync(MeshQueryRequest.FromQuery("path:item nodeType:Item scope:descendants limit:100"))
            .OfType<MeshNode>()
            .ToListAsync();

        // Assert
        firstPage.Should().HaveCount(3);
        secondPage.Should().HaveCount(6);
        allItems.Should().HaveCount(10);
    }

    [Fact]
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
            });
        }

        // Act - request limit+1 to detect if there are more
        var limit = 3;
        var queryLimit = limit + 1;
        var results = await meshQuery.QueryAsync(MeshQueryRequest.FromQuery($"path:test nodeType:Test scope:descendants limit:{queryLimit}"))
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

    [Fact]
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
        });
        await innerPersistence.SaveNodeAsync(MeshNode.FromPath("data/doc1") with
        {
            Name = "Document One",
            NodeType = "Document"
        });
        await innerPersistence.SaveNodeAsync(MeshNode.FromPath("data/project2") with
        {
            Name = "Project Beta",
            NodeType = "Project"
        });

        // Access all nodes to create activity
        using (var decorator = new ActivityTrackingPersistenceDecorator(
            innerPersistence, accessService, NullLogger<ActivityTrackingPersistenceDecorator>.Instance))
        {
            await decorator.GetNodeAsync("data/project1");
            await Task.Delay(10);
            await decorator.GetNodeAsync("data/doc1");
            await Task.Delay(10);
            await decorator.GetNodeAsync("data/project2");
        }

        // Act - get activity records and filter by nodeType manually (as catalog would do)
        var allActivityRecords = await innerPersistence.GetPartitionObjectsAsync("_activity/search-user")
            .OfType<UserActivityRecord>()
            .OrderByDescending(a => a.LastAccessedAt)
            .ToListAsync();

        // Load nodes and filter by type
        var projectRecords = new List<UserActivityRecord>();
        foreach (var activity in allActivityRecords)
        {
            var node = await innerPersistence.GetNodeAsync(activity.NodePath);
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
