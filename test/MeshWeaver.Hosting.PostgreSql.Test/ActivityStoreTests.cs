using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Mesh.Activity;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Tests for PostgreSqlActivityStore using the dedicated user_activity table.
/// </summary>
[Collection("PostgreSql")]
public class ActivityStoreTests
{
    private readonly PostgreSqlFixture _fixture;

    public ActivityStoreTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SaveAndRetrieve_SingleActivity()
    {
        await _fixture.CleanDataAsync();
        var store = _fixture.ActivityStore;

        var record = new UserActivityRecord
        {
            Id = "org_acme",
            NodePath = "org/acme",
            UserId = "alice",
            ActivityType = ActivityType.Read,
            FirstAccessedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            LastAccessedAt = DateTimeOffset.UtcNow,
            AccessCount = 1,
            NodeName = "Acme Corp",
            NodeType = "Organization",
            Namespace = "org"
        };

        await store.SaveActivitiesAsync("alice", [record], TestContext.Current.CancellationToken);

        var results = await store.GetActivitiesAsync("alice", TestContext.Current.CancellationToken);
        results.Should().ContainSingle();
        var loaded = results[0];
        loaded.NodePath.Should().Be("org/acme");
        loaded.UserId.Should().Be("alice");
        loaded.ActivityType.Should().Be(ActivityType.Read);
        loaded.AccessCount.Should().Be(1);
        loaded.NodeName.Should().Be("Acme Corp");
        loaded.NodeType.Should().Be("Organization");
        loaded.Namespace.Should().Be("org");
    }

    [Fact]
    public async Task Upsert_IncrementsAccessCount()
    {
        await _fixture.CleanDataAsync();
        var store = _fixture.ActivityStore;
        var now = DateTimeOffset.UtcNow;

        // First save
        await store.SaveActivitiesAsync("bob", [new UserActivityRecord
        {
            Id = "doc_report",
            NodePath = "doc/report",
            UserId = "bob",
            ActivityType = ActivityType.Read,
            FirstAccessedAt = now.AddMinutes(-10),
            LastAccessedAt = now.AddMinutes(-5),
            AccessCount = 1,
            NodeName = "Report"
        }], TestContext.Current.CancellationToken);

        // Second save (upsert)
        await store.SaveActivitiesAsync("bob", [new UserActivityRecord
        {
            Id = "doc_report",
            NodePath = "doc/report",
            UserId = "bob",
            ActivityType = ActivityType.Write,
            FirstAccessedAt = now,
            LastAccessedAt = now,
            AccessCount = 1,
            NodeName = "Report Updated"
        }], TestContext.Current.CancellationToken);

        var results = await store.GetActivitiesAsync("bob", TestContext.Current.CancellationToken);
        results.Should().ContainSingle();
        var loaded = results[0];
        loaded.AccessCount.Should().Be(2); // 1 + 1
        loaded.ActivityType.Should().Be(ActivityType.Write); // Updated to latest
        loaded.NodeName.Should().Be("Report Updated");
    }

    [Fact]
    public async Task MultipleActivities_OrderedByLastAccessed()
    {
        await _fixture.CleanDataAsync();
        var store = _fixture.ActivityStore;
        var now = DateTimeOffset.UtcNow;

        await store.SaveActivitiesAsync("charlie", [
            new UserActivityRecord
            {
                Id = "a_first",
                NodePath = "a/first",
                UserId = "charlie",
                ActivityType = ActivityType.Read,
                FirstAccessedAt = now.AddMinutes(-30),
                LastAccessedAt = now.AddMinutes(-30),
                AccessCount = 1,
                NodeName = "First"
            },
            new UserActivityRecord
            {
                Id = "b_second",
                NodePath = "b/second",
                UserId = "charlie",
                ActivityType = ActivityType.Read,
                FirstAccessedAt = now.AddMinutes(-10),
                LastAccessedAt = now.AddMinutes(-10),
                AccessCount = 1,
                NodeName = "Second"
            },
            new UserActivityRecord
            {
                Id = "c_third",
                NodePath = "c/third",
                UserId = "charlie",
                ActivityType = ActivityType.Read,
                FirstAccessedAt = now.AddMinutes(-1),
                LastAccessedAt = now.AddMinutes(-1),
                AccessCount = 1,
                NodeName = "Third"
            }
        ], TestContext.Current.CancellationToken);

        var results = await store.GetActivitiesAsync("charlie", TestContext.Current.CancellationToken);
        results.Should().HaveCount(3);
        // Should be ordered by last_accessed DESC
        results[0].NodePath.Should().Be("c/third");
        results[1].NodePath.Should().Be("b/second");
        results[2].NodePath.Should().Be("a/first");
    }

    [Fact]
    public async Task DifferentUsers_IsolatedActivities()
    {
        await _fixture.CleanDataAsync();
        var store = _fixture.ActivityStore;
        var now = DateTimeOffset.UtcNow;

        await store.SaveActivitiesAsync("alice", [new UserActivityRecord
        {
            Id = "shared_node",
            NodePath = "shared/node",
            UserId = "alice",
            ActivityType = ActivityType.Read,
            FirstAccessedAt = now,
            LastAccessedAt = now,
            AccessCount = 3,
            NodeName = "Shared"
        }], TestContext.Current.CancellationToken);

        await store.SaveActivitiesAsync("bob", [new UserActivityRecord
        {
            Id = "shared_node",
            NodePath = "shared/node",
            UserId = "bob",
            ActivityType = ActivityType.Write,
            FirstAccessedAt = now,
            LastAccessedAt = now,
            AccessCount = 1,
            NodeName = "Shared"
        }], TestContext.Current.CancellationToken);

        var aliceActivities = await store.GetActivitiesAsync("alice", TestContext.Current.CancellationToken);
        var bobActivities = await store.GetActivitiesAsync("bob", TestContext.Current.CancellationToken);

        aliceActivities.Should().ContainSingle();
        aliceActivities[0].AccessCount.Should().Be(3);
        aliceActivities[0].ActivityType.Should().Be(ActivityType.Read);

        bobActivities.Should().ContainSingle();
        bobActivities[0].AccessCount.Should().Be(1);
        bobActivities[0].ActivityType.Should().Be(ActivityType.Write);
    }

    [Fact]
    public async Task EmptyUser_ReturnsEmptyList()
    {
        await _fixture.CleanDataAsync();
        var store = _fixture.ActivityStore;

        var results = await store.GetActivitiesAsync("nonexistent-user", TestContext.Current.CancellationToken);
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task BatchUpsert_MultipleRecords()
    {
        await _fixture.CleanDataAsync();
        var store = _fixture.ActivityStore;
        var now = DateTimeOffset.UtcNow;

        // Save initial batch
        await store.SaveActivitiesAsync("dave", [
            new UserActivityRecord { Id = "n1", NodePath = "n/1", UserId = "dave", ActivityType = ActivityType.Read, FirstAccessedAt = now, LastAccessedAt = now, AccessCount = 1, NodeName = "Node 1" },
            new UserActivityRecord { Id = "n2", NodePath = "n/2", UserId = "dave", ActivityType = ActivityType.Read, FirstAccessedAt = now, LastAccessedAt = now, AccessCount = 1, NodeName = "Node 2" },
            new UserActivityRecord { Id = "n3", NodePath = "n/3", UserId = "dave", ActivityType = ActivityType.Read, FirstAccessedAt = now, LastAccessedAt = now, AccessCount = 1, NodeName = "Node 3" }
        ], TestContext.Current.CancellationToken);

        // Upsert n1 and n2, add n4
        await store.SaveActivitiesAsync("dave", [
            new UserActivityRecord { Id = "n1", NodePath = "n/1", UserId = "dave", ActivityType = ActivityType.Write, FirstAccessedAt = now, LastAccessedAt = now.AddSeconds(1), AccessCount = 2, NodeName = "Node 1 Updated" },
            new UserActivityRecord { Id = "n2", NodePath = "n/2", UserId = "dave", ActivityType = ActivityType.Read, FirstAccessedAt = now, LastAccessedAt = now.AddSeconds(2), AccessCount = 1, NodeName = "Node 2" },
            new UserActivityRecord { Id = "n4", NodePath = "n/4", UserId = "dave", ActivityType = ActivityType.Read, FirstAccessedAt = now, LastAccessedAt = now.AddSeconds(3), AccessCount = 1, NodeName = "Node 4" }
        ], TestContext.Current.CancellationToken);

        var results = await store.GetActivitiesAsync("dave", TestContext.Current.CancellationToken);
        results.Should().HaveCount(4);

        var n1 = results.First(r => r.NodePath == "n/1");
        n1.AccessCount.Should().Be(3); // 1 + 2
        n1.ActivityType.Should().Be(ActivityType.Write);
        n1.NodeName.Should().Be("Node 1 Updated");

        var n2 = results.First(r => r.NodePath == "n/2");
        n2.AccessCount.Should().Be(2); // 1 + 1

        var n3 = results.First(r => r.NodePath == "n/3");
        n3.AccessCount.Should().Be(1); // Unchanged

        var n4 = results.First(r => r.NodePath == "n/4");
        n4.AccessCount.Should().Be(1); // New
    }

    [Fact]
    public async Task NullableFields_HandledCorrectly()
    {
        await _fixture.CleanDataAsync();
        var store = _fixture.ActivityStore;
        var now = DateTimeOffset.UtcNow;

        // Save with null optional fields
        await store.SaveActivitiesAsync("eve", [new UserActivityRecord
        {
            Id = "minimal",
            NodePath = "minimal/node",
            UserId = "eve",
            ActivityType = ActivityType.Delete,
            FirstAccessedAt = now,
            LastAccessedAt = now,
            AccessCount = 1,
            NodeName = null,
            NodeType = null,
            Namespace = null
        }], TestContext.Current.CancellationToken);

        var results = await store.GetActivitiesAsync("eve", TestContext.Current.CancellationToken);
        results.Should().ContainSingle();
        var loaded = results[0];
        loaded.NodeName.Should().BeNull();
        loaded.NodeType.Should().BeNull();
        loaded.Namespace.Should().BeNull();
        loaded.ActivityType.Should().Be(ActivityType.Delete);
    }
}
