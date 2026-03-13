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
/// Tests for source:accessed queries via PostgreSqlMeshQuery using INNER JOIN on UserActivity MeshNodes,
/// and source:activity queries that imply nodeType:Activity filter.
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
        var ac = _fixture.AccessControl;

        // Seed content nodes
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

        // Grant Public read access
        await ac.GrantAsync("org", "Public", "Read", isAllow: true, TestContext.Current.CancellationToken);

        var now = DateTimeOffset.UtcNow;

        // Seed UserActivity MeshNodes at User/alice/_UserActivity/{encodedPath}
        // Thread3 most recent, Thread1 second, Thread2 oldest
        await adapter.WriteAsync(new MeshNode("org_ACME_Discussions_Thread2", "User/alice/_UserActivity")
        {
            Name = "Thread Two",
            NodeType = "UserActivity",
            MainNode = "User/alice",
            Content = new UserActivityRecord
            {
                Id = "org_ACME_Discussions_Thread2",
                NodePath = "org/ACME/Discussions/Thread2",
                UserId = "alice",
                ActivityType = ActivityType.Read,
                FirstAccessedAt = now.AddMinutes(-30),
                LastAccessedAt = now.AddMinutes(-30),
                AccessCount = 1,
            }
        }, _options, TestContext.Current.CancellationToken);

        // Small delay to ensure distinct last_modified timestamps
        await Task.Delay(10);

        await adapter.WriteAsync(new MeshNode("org_ACME_Discussions_Thread1", "User/alice/_UserActivity")
        {
            Name = "Thread One",
            NodeType = "UserActivity",
            MainNode = "User/alice",
            Content = new UserActivityRecord
            {
                Id = "org_ACME_Discussions_Thread1",
                NodePath = "org/ACME/Discussions/Thread1",
                UserId = "alice",
                ActivityType = ActivityType.Read,
                FirstAccessedAt = now.AddMinutes(-10),
                LastAccessedAt = now.AddMinutes(-10),
                AccessCount = 2,
            }
        }, _options, TestContext.Current.CancellationToken);

        await Task.Delay(10);

        await adapter.WriteAsync(new MeshNode("org_ACME_Discussions_Thread3", "User/alice/_UserActivity")
        {
            Name = "Thread Three",
            NodeType = "UserActivity",
            MainNode = "User/alice",
            Content = new UserActivityRecord
            {
                Id = "org_ACME_Discussions_Thread3",
                NodePath = "org/ACME/Discussions/Thread3",
                UserId = "alice",
                ActivityType = ActivityType.Read,
                FirstAccessedAt = now.AddMinutes(-1),
                LastAccessedAt = now.AddMinutes(-1),
                AccessCount = 5,
            }
        }, _options, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AccessedQuery_OrdersByLastModifiedDescending()
    {
        await SeedNodesAndActivityAsync();

        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = new MeshQueryRequest
        {
            Query = "source:accessed nodeType:Thread namespace:org/ACME/Discussions",
            UserId = "alice"
        };

        var results = new List<MeshNode>();
        await foreach (var item in query.QueryAsync(request, _options, TestContext.Current.CancellationToken))
        {
            if (item is MeshNode node)
                results.Add(node);
        }

        results.Should().HaveCount(3);
        // Thread3 most recent UserActivity node, Thread1 second, Thread2 oldest
        results[0].Id.Should().Be("Thread3");
        results[1].Id.Should().Be("Thread1");
        results[2].Id.Should().Be("Thread2");
    }

    [Fact]
    public async Task AccessedQuery_RespectsNodeTypeFilter()
    {
        await SeedNodesAndActivityAsync();

        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = new MeshQueryRequest
        {
            // Doc1 has no UserActivity record — should not appear with INNER JOIN
            Query = "source:accessed nodeType:Document path:org scope:descendants",
            UserId = "alice"
        };

        var results = new List<MeshNode>();
        await foreach (var item in query.QueryAsync(request, _options, TestContext.Current.CancellationToken))
        {
            if (item is MeshNode node)
                results.Add(node);
        }

        // Doc1 has no UserActivity — INNER JOIN excludes it
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task AccessedQuery_RespectsLimit()
    {
        await SeedNodesAndActivityAsync();

        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = new MeshQueryRequest
        {
            Query = "source:accessed path:org scope:descendants limit:2",
            UserId = "alice"
        };

        var results = new List<MeshNode>();
        await foreach (var item in query.QueryAsync(request, _options, TestContext.Current.CancellationToken))
        {
            if (item is MeshNode node)
                results.Add(node);
        }

        results.Should().HaveCount(2);
        results[0].Id.Should().Be("Thread3");
        results[1].Id.Should().Be("Thread1");
    }

    [Fact]
    public async Task AccessedQuery_DifferentUser_SeesOwnActivity()
    {
        await SeedNodesAndActivityAsync();

        // bob has no UserActivity nodes — INNER JOIN returns nothing
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = new MeshQueryRequest
        {
            Query = "source:accessed nodeType:Thread namespace:org/ACME/Discussions",
            UserId = "bob"
        };

        var results = new List<MeshNode>();
        await foreach (var item in query.QueryAsync(request, _options, TestContext.Current.CancellationToken))
        {
            if (item is MeshNode node)
                results.Add(node);
        }

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task NonAccessedQuery_IgnoresActivityOrdering()
    {
        await SeedNodesAndActivityAsync();

        // Normal query without source:accessed should not join UserActivity
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
