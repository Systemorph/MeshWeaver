using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Activity;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Tests for activity-ordered queries via PostgreSqlMeshQuery using LEFT JOIN on user_activity.
/// </summary>
[Collection("PostgreSql")]
public class ActivityOrderedQueryTests
{
    private readonly PostgreSqlFixture _fixture;
    private readonly JsonSerializerOptions _options = new();

    public ActivityOrderedQueryTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task SeedNodesAndActivityAsync()
    {
        await _fixture.CleanDataAsync();
        var adapter = _fixture.StorageAdapter;
        var activityStore = _fixture.ActivityStore;
        var ac = _fixture.AccessControl;

        // Seed nodes
        await adapter.WriteAsync(new MeshNode("Thread1", "org/ACME/Discussions")
        {
            Name = "Thread One",
            NodeType = "Thread"
        }, _options, TestContext.Current.CancellationToken);

        await adapter.WriteAsync(new MeshNode("Thread2", "org/ACME/Discussions")
        {
            Name = "Thread Two",
            NodeType = "Thread"
        }, _options, TestContext.Current.CancellationToken);

        await adapter.WriteAsync(new MeshNode("Thread3", "org/ACME/Discussions")
        {
            Name = "Thread Three",
            NodeType = "Thread"
        }, _options, TestContext.Current.CancellationToken);

        await adapter.WriteAsync(new MeshNode("Doc1", "org/ACME/Docs")
        {
            Name = "Document One",
            NodeType = "Document"
        }, _options, TestContext.Current.CancellationToken);

        // Grant Public read access (all authenticated users inherit Public permissions)
        await ac.GrantAsync("org", "Public", "Read", isAllow: true, TestContext.Current.CancellationToken);

        var now = DateTimeOffset.UtcNow;

        // Seed activity: Thread3 most recent, Thread1 second, Thread2 oldest
        await activityStore.SaveActivitiesAsync("alice", [
            new UserActivityRecord
            {
                Id = "t2",
                NodePath = "org/ACME/Discussions/Thread2",
                UserId = "alice",
                ActivityType = ActivityType.Read,
                FirstAccessedAt = now.AddMinutes(-30),
                LastAccessedAt = now.AddMinutes(-30),
                AccessCount = 1,
                NodeName = "Thread Two",
                NodeType = "Thread",
                Namespace = "org/ACME/Discussions"
            },
            new UserActivityRecord
            {
                Id = "t1",
                NodePath = "org/ACME/Discussions/Thread1",
                UserId = "alice",
                ActivityType = ActivityType.Read,
                FirstAccessedAt = now.AddMinutes(-10),
                LastAccessedAt = now.AddMinutes(-10),
                AccessCount = 2,
                NodeName = "Thread One",
                NodeType = "Thread",
                Namespace = "org/ACME/Discussions"
            },
            new UserActivityRecord
            {
                Id = "t3",
                NodePath = "org/ACME/Discussions/Thread3",
                UserId = "alice",
                ActivityType = ActivityType.Read,
                FirstAccessedAt = now.AddMinutes(-1),
                LastAccessedAt = now.AddMinutes(-1),
                AccessCount = 5,
                NodeName = "Thread Three",
                NodeType = "Thread",
                Namespace = "org/ACME/Discussions"
            }
        ], TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ActivityQuery_OrdersByLastAccessedDescending()
    {
        await SeedNodesAndActivityAsync();

        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = new MeshQueryRequest
        {
            Query = "source:activity nodeType:Thread namespace:org/ACME/Discussions",
            UserId = "alice"
        };

        var results = new List<MeshNode>();
        await foreach (var item in query.QueryAsync(request, _options, TestContext.Current.CancellationToken))
        {
            if (item is MeshNode node)
                results.Add(node);
        }

        results.Should().HaveCount(3);
        // Thread3 most recent (1 min ago), Thread1 (10 min ago), Thread2 (30 min ago)
        results[0].Id.Should().Be("Thread3");
        results[1].Id.Should().Be("Thread1");
        results[2].Id.Should().Be("Thread2");
    }

    [Fact]
    public async Task ActivityQuery_NodesWithoutActivity_AppearAfterActivityNodes()
    {
        await SeedNodesAndActivityAsync();

        // Query all nodes under org scope:descendants — includes Doc1 which has no activity
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = new MeshQueryRequest
        {
            Query = "source:activity path:org scope:descendants",
            UserId = "alice"
        };

        var results = new List<MeshNode>();
        await foreach (var item in query.QueryAsync(request, _options, TestContext.Current.CancellationToken))
        {
            if (item is MeshNode node)
                results.Add(node);
        }

        results.Should().HaveCount(4);
        // First 3 should be activity-tracked (ordered by last_accessed DESC)
        results[0].Id.Should().Be("Thread3");
        results[1].Id.Should().Be("Thread1");
        results[2].Id.Should().Be("Thread2");
        // Doc1 has no activity — should appear last (NULLS LAST)
        results[3].Id.Should().Be("Doc1");
    }

    [Fact]
    public async Task ActivityQuery_RespectsNodeTypeFilter()
    {
        await SeedNodesAndActivityAsync();

        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = new MeshQueryRequest
        {
            Query = "source:activity nodeType:Document path:org scope:descendants",
            UserId = "alice"
        };

        var results = new List<MeshNode>();
        await foreach (var item in query.QueryAsync(request, _options, TestContext.Current.CancellationToken))
        {
            if (item is MeshNode node)
                results.Add(node);
        }

        // Only Doc1 matches nodeType:Document
        results.Should().ContainSingle();
        results[0].Id.Should().Be("Doc1");
    }

    [Fact]
    public async Task ActivityQuery_RespectsNamespaceFilter()
    {
        await SeedNodesAndActivityAsync();

        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = new MeshQueryRequest
        {
            Query = "source:activity namespace:org/ACME/Docs",
            UserId = "alice"
        };

        var results = new List<MeshNode>();
        await foreach (var item in query.QueryAsync(request, _options, TestContext.Current.CancellationToken))
        {
            if (item is MeshNode node)
                results.Add(node);
        }

        // Only Doc1 is under org/ACME/Docs namespace
        results.Should().ContainSingle();
        results[0].Id.Should().Be("Doc1");
    }

    [Fact]
    public async Task ActivityQuery_RespectsLimit()
    {
        await SeedNodesAndActivityAsync();

        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = new MeshQueryRequest
        {
            Query = "source:activity path:org scope:descendants limit:2",
            UserId = "alice"
        };

        var results = new List<MeshNode>();
        await foreach (var item in query.QueryAsync(request, _options, TestContext.Current.CancellationToken))
        {
            if (item is MeshNode node)
                results.Add(node);
        }

        results.Should().HaveCount(2);
        // Should be the top 2 by activity order
        results[0].Id.Should().Be("Thread3");
        results[1].Id.Should().Be("Thread1");
    }

    [Fact]
    public async Task ActivityQuery_DifferentUser_SeesOwnActivity()
    {
        await SeedNodesAndActivityAsync();

        // bob has no activity records — all nodes should appear (NULLS LAST ordering)
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = new MeshQueryRequest
        {
            Query = "source:activity nodeType:Thread namespace:org/ACME/Discussions",
            UserId = "bob"
        };

        var results = new List<MeshNode>();
        await foreach (var item in query.QueryAsync(request, _options, TestContext.Current.CancellationToken))
        {
            if (item is MeshNode node)
                results.Add(node);
        }

        // All 3 threads returned (no activity for bob, all NULL last_accessed)
        results.Should().HaveCount(3);
    }

    [Fact]
    public async Task NonActivityQuery_IgnoresActivityOrdering()
    {
        await SeedNodesAndActivityAsync();

        // Normal query without source:activity should not join user_activity
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = new MeshQueryRequest
        {
            Query = "nodeType:Thread namespace:org/ACME/Discussions sort:name",
            UserId = "alice"
        };

        var results = new List<MeshNode>();
        await foreach (var item in query.QueryAsync(request, _options, TestContext.Current.CancellationToken))
        {
            if (item is MeshNode node)
                results.Add(node);
        }

        results.Should().HaveCount(3);
        // Should be alphabetical by name: One, Three, Two
        results[0].Name.Should().Be("Thread One");
        results[1].Name.Should().Be("Thread Three");
        results[2].Name.Should().Be("Thread Two");
    }
}
