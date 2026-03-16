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
/// Tests that PostgreSqlMeshQuery can find satellite nodes in their dedicated tables
/// when querying by nodeType. This validates the dashboard queries used by
/// UserActivityLayoutAreas.cs: nodeType:Thread, source:activity, etc.
/// </summary>
[Collection("PostgreSql")]
public class SatelliteQueryTests : IAsyncLifetime
{
    private readonly PostgreSqlFixture _fixture;
    private readonly JsonSerializerOptions _options = new();
    private Npgsql.NpgsqlDataSource _schemaDs = null!;
    private PostgreSqlStorageAdapter _adapter = null!;
    private PostgreSqlMeshQuery _query = null!;

    private static readonly PartitionDefinition UserPartition = new()
    {
        Namespace = "User",
        DataSource = "default",
        Schema = "user_sat_query_test",
        TableMappings = PartitionDefinition.StandardTableMappings,
    };

    public SatelliteQueryTests(PostgreSqlFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var (ds, adapter) = await _fixture.CreateSchemaAdapterAsync(
            "user_sat_query_test", UserPartition, ct);
        _schemaDs = ds;
        _adapter = adapter;
        _query = new PostgreSqlMeshQuery(_adapter);

        // Grant Anonymous read access so queries work without explicit userId
        var ac = _fixture.AccessControl;
        await ac.GrantAsync("User", "Anonymous", "Read", isAllow: true, ct);
    }

    public ValueTask DisposeAsync()
    {
        _schemaDs?.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<List<MeshNode>> QueryAsync(string query, string? defaultPath = null)
    {
        var request = string.IsNullOrEmpty(defaultPath)
            ? MeshQueryRequest.FromQuery(query)
            : new MeshQueryRequest { Query = query, DefaultPath = defaultPath };
        var results = new List<MeshNode>();
        await foreach (var item in _query.QueryAsync(request, _options, TestContext.Current.CancellationToken))
        {
            if (item is MeshNode node)
                results.Add(node);
        }
        return results;
    }

    /// <summary>Direct SQL query for debugging — bypasses the MeshQuery layer.</summary>
    private async Task<List<MeshNode>> RawQueryAsync(string tableName, string? nodeType = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var sql = $"SELECT id, namespace, name, node_type FROM \"{tableName}\"";
        if (nodeType != null)
            sql += $" WHERE node_type = '{nodeType}'";
        await using var cmd = _schemaDs.CreateCommand(sql);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<MeshNode>();
        while (await reader.ReadAsync(ct))
        {
            results.Add(new MeshNode(reader.GetString(0), reader.GetString(1))
            {
                Name = reader.IsDBNull(2) ? null : reader.GetString(2),
                NodeType = reader.IsDBNull(3) ? null : reader.GetString(3),
            });
        }
        return results;
    }

    // ── Thread ──────────────────────────────────────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task NodeType_Thread_FindsInThreadsTable()
    {
        var ct = TestContext.Current.CancellationToken;

        // Seed a thread in the threads table
        await _adapter.WriteAsync(new MeshNode("hello-world", "User/alice/_Thread")
        {
            Name = "Hello World Thread",
            NodeType = "Thread",
            MainNode = "User/alice/_Thread",
        }, _options, ct);

        // Verify data was written to the threads table
        await using var cmd = _schemaDs.CreateCommand(
            "SELECT COUNT(*) FROM threads WHERE id = 'hello-world'");
        var count = (long)(await cmd.ExecuteScalarAsync(ct))!;
        count.Should().Be(1, "thread should exist in threads table");

        // Verify data is NOT in mesh_nodes
        await using var mnCmd = _schemaDs.CreateCommand(
            "SELECT COUNT(*) FROM mesh_nodes WHERE id = 'hello-world'");
        var mnCount = (long)(await mnCmd.ExecuteScalarAsync(ct))!;

        // Query by nodeType only (no path) — must resolve to threads table
        var results = await QueryAsync("nodeType:Thread sort:LastModified-desc");

        // Raw SQL verification: data IS in threads table
        var rawThreads = await RawQueryAsync("threads", "Thread");
        rawThreads.Should().NotBeEmpty("raw SQL confirms threads exist in threads table");

        // Raw SQL: data is NOT in mesh_nodes
        var rawMeshNodes = await RawQueryAsync("mesh_nodes", "Thread");

        // PostgreSqlMeshQuery must find the data in threads table
        results.Should().NotBeEmpty(
            $"nodeType:Thread should find threads. Raw threads table has {rawThreads.Count} rows, " +
            $"mesh_nodes has {rawMeshNodes.Count} Thread rows, query returned {results.Count}");
    }

    [Fact(Timeout = 30000)]
    public async Task NodeType_Thread_WithDefaultPath_FindsInThreadsTable()
    {
        var ct = TestContext.Current.CancellationToken;

        await _adapter.WriteAsync(new MeshNode("help-me", "User/bob/_Thread")
        {
            Name = "Help Me Thread",
            NodeType = "Thread",
            MainNode = "User/bob/_Thread",
        }, _options, ct);

        // Simulate routing fan-out: DefaultPath = "User", scope:descendants
        var results = await QueryAsync(
            "nodeType:Thread sort:LastModified-desc scope:descendants",
            defaultPath: "User");

        results.Should().NotBeEmpty("routing fan-out with DefaultPath=User should find threads");
        results.Should().Contain(n => n.Name == "Help Me Thread");
    }

    [Fact(Timeout = 30000)]
    public async Task Namespace_Thread_FindsThreads()
    {
        var ct = TestContext.Current.CancellationToken;

        await _adapter.WriteAsync(new MeshNode("ns-thread", "User/carol/_Thread")
        {
            Name = "Namespace Thread",
            NodeType = "Thread",
            MainNode = "User/carol/_Thread",
        }, _options, ct);

        // Direct namespace query
        var results = await QueryAsync("namespace:User/carol/_Thread nodeType:Thread");
        results.Should().NotBeEmpty("namespace query to _Thread should find threads");
        results.Should().Contain(n => n.Name == "Namespace Thread");
    }

    // ── ThreadMessage ───────────────────────────────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task NodeType_ThreadMessage_FindsInThreadsTable()
    {
        var ct = TestContext.Current.CancellationToken;

        await _adapter.WriteAsync(new MeshNode("msg-1", "User/alice/_Thread/hello-world")
        {
            Name = "User message",
            NodeType = "ThreadMessage",
            MainNode = "User/alice/_Thread/hello-world",
        }, _options, ct);

        var results = await QueryAsync("nodeType:ThreadMessage scope:descendants", defaultPath: "User");
        results.Should().NotBeEmpty("nodeType:ThreadMessage should find messages in threads table");
    }

    // ── Activity ────────────────────────────────────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task NodeType_Activity_FindsInActivitiesTable()
    {
        var ct = TestContext.Current.CancellationToken;

        // Seed main node + activity satellite
        await _adapter.WriteAsync(new MeshNode("doc1", "User/alice")
        {
            Name = "Alice Doc",
            NodeType = "Markdown",
        }, _options, ct);

        await _adapter.WriteAsync(new MeshNode("log1", "User/alice/doc1/_Activity")
        {
            Name = "Activity Log",
            NodeType = "Activity",
            MainNode = "User/alice/doc1",
        }, _options, ct);

        var results = await QueryAsync("nodeType:Activity scope:descendants", defaultPath: "User");
        results.Should().NotBeEmpty("nodeType:Activity should find records in activities table");
        results.Should().Contain(n => n.NodeType == "Activity");
    }

    [Fact(Timeout = 30000)]
    public async Task SourceActivity_FindsMainNodesWithActivitySatellites()
    {
        var ct = TestContext.Current.CancellationToken;

        // Main node
        await _adapter.WriteAsync(new MeshNode("project1", "User/alice")
        {
            Name = "Alice Project",
            NodeType = "Markdown",
        }, _options, ct);

        // Activity satellite
        await _adapter.WriteAsync(new MeshNode("act1", "User/alice/project1/_Activity")
        {
            Name = "Project Activity",
            NodeType = "Activity",
            MainNode = "User/alice/project1",
        }, _options, ct);

        // Dashboard query: source:activity
        var results = await QueryAsync(
            "source:activity scope:descendants sort:LastModified-desc",
            defaultPath: "User");

        // source:activity returns MAIN nodes that have activity children
        var mainResults = results.Where(n => n.NodeType != "Activity").ToList();
        mainResults.Should().NotBeEmpty(
            "source:activity should return main nodes with activity satellites");
    }

    // ── UserActivity ────────────────────────────────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task NodeType_UserActivity_FindsInUserActivitiesTable()
    {
        var ct = TestContext.Current.CancellationToken;

        await _adapter.WriteAsync(new MeshNode("User_alice_doc1", "User/alice/_UserActivity")
        {
            Name = "Doc1 access",
            NodeType = "UserActivity",
            MainNode = "User/alice",
            Content = new UserActivityRecord
            {
                Id = "User_alice_doc1",
                NodePath = "User/alice/doc1",
                UserId = "alice",
                ActivityType = ActivityType.Read,
                FirstAccessedAt = DateTimeOffset.UtcNow.AddHours(-1),
                LastAccessedAt = DateTimeOffset.UtcNow,
                AccessCount = 5,
            }
        }, _options, ct);

        var results = await QueryAsync("nodeType:UserActivity scope:descendants", defaultPath: "User");
        results.Should().NotBeEmpty("nodeType:UserActivity should find records in user_activities table");
    }

    // ── AccessAssignment ────────────────────────────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task NodeType_AccessAssignment_FindsInAccessTable()
    {
        var ct = TestContext.Current.CancellationToken;

        await _adapter.WriteAsync(new MeshNode("aa1", "User/alice/_Access")
        {
            Name = "Admin role",
            NodeType = "AccessAssignment",
            MainNode = "User/alice",
        }, _options, ct);

        var results = await QueryAsync("nodeType:AccessAssignment scope:descendants", defaultPath: "User");
        results.Should().NotBeEmpty("nodeType:AccessAssignment should find records in access table");
    }

    // ── Comment ─────────────────────────────────────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task NodeType_Comment_FindsInAnnotationsTable()
    {
        var ct = TestContext.Current.CancellationToken;

        await _adapter.WriteAsync(new MeshNode("cmt1", "User/alice/doc1/_Comment")
        {
            Name = "Great doc!",
            NodeType = "Comment",
            MainNode = "User/alice/doc1",
        }, _options, ct);

        var results = await QueryAsync("nodeType:Comment scope:descendants", defaultPath: "User");
        results.Should().NotBeEmpty("nodeType:Comment should find records in annotations table");
    }

    // ── TrackedChange ───────────────────────────────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task NodeType_TrackedChange_FindsInAnnotationsTable()
    {
        var ct = TestContext.Current.CancellationToken;

        await _adapter.WriteAsync(new MeshNode("tc1", "User/alice/doc1/_Tracking")
        {
            Name = "Section updated",
            NodeType = "TrackedChange",
            MainNode = "User/alice/doc1",
        }, _options, ct);

        var results = await QueryAsync("nodeType:TrackedChange scope:descendants", defaultPath: "User");
        results.Should().NotBeEmpty("nodeType:TrackedChange should find records in annotations table");
    }

    // ── Approval ────────────────────────────────────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task NodeType_Approval_FindsInAnnotationsTable()
    {
        var ct = TestContext.Current.CancellationToken;

        await _adapter.WriteAsync(new MeshNode("apr1", "User/alice/doc1/_Approval")
        {
            Name = "Approved by Bob",
            NodeType = "Approval",
            MainNode = "User/alice/doc1",
        }, _options, ct);

        var results = await QueryAsync("nodeType:Approval scope:descendants", defaultPath: "User");
        results.Should().NotBeEmpty("nodeType:Approval should find records in annotations table");
    }

    // ── Code ────────────────────────────────────────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task NodeType_Code_FindsInCodeTable()
    {
        var ct = TestContext.Current.CancellationToken;

        await _adapter.WriteAsync(new MeshNode("MyClass", "User/alice/project/_Source")
        {
            Name = "MyClass.cs",
            NodeType = "Code",
            MainNode = "User/alice/project",
        }, _options, ct);

        var results = await QueryAsync("nodeType:Code scope:descendants", defaultPath: "User");
        results.Should().NotBeEmpty("nodeType:Code should find records in code table");
    }
}
