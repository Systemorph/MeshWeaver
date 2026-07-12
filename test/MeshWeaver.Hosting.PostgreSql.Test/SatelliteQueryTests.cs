using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Activity;
using MeshWeaver.Mesh.Services;
using Xunit;
using MeshWeaver.Fixture;

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

    private static readonly PartitionDefinition UserPartition = new()
    {
        Namespace = "User",
        DataSource = "default",
        Schema = "user_sat_query_test",
        TableMappings = PartitionDefinition.DefaultSegmentTableMappings(), NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings(),
    };

    public SatelliteQueryTests(PostgreSqlFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync()
    {
        var (ds, adapter) = await _fixture.CreateSchemaAdapterAsync(
            "user_sat_query_test", UserPartition, TestContext.Current.CancellationToken);
        _schemaDs = ds;
        _adapter = adapter;
    }

    public ValueTask DisposeAsync()
    {
        _schemaDs?.Dispose();
        return ValueTask.CompletedTask;
    }

    private Task Write(MeshNode node)
        => _adapter.Write(node, _options).Should().Within(30.Seconds()).Emit();

    private Task<List<MeshNode>> Query(string queryString, string? defaultPath = null)
    {
        // Use adapter.QueryNodesAsync directly (no access control) to test table resolution
        var parser = new QueryParser();
        var parsedQuery = parser.Parse(queryString);

        var effectivePath = parsedQuery.Path;
        var effectiveScope = parsedQuery.Scope;
        if (string.IsNullOrEmpty(effectivePath))
        {
            if (!string.IsNullOrEmpty(defaultPath))
                effectivePath = defaultPath;
            if (parsedQuery.Scope == QueryScope.Exact)
                effectiveScope = QueryScope.Children;
        }
        parsedQuery = parsedQuery with { Path = effectivePath, Scope = effectiveScope };

        return _adapter.QueryNodesAsync(parsedQuery, _options,
                userId: null, ct: TestContext.Current.CancellationToken)
            .Collect(TestContext.Current.CancellationToken)
            .Should().Within(30.Seconds()).Emit();
    }

    /// <summary>Direct SQL query for debugging — bypasses the MeshQuery layer.</summary>
    private Task<List<MeshNode>> RawQuery(string tableName, string? nodeType = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var sql = $"SELECT id, namespace, name, node_type FROM \"{tableName}\"";
        if (nodeType != null)
            sql += $" WHERE node_type = '{nodeType}'";
        return _schemaDs.Rows(sql, System.Array.Empty<(string, object)>(),
                reader => new MeshNode(reader.GetString(0), reader.GetString(1))
                {
                    Name = reader.IsDBNull(2) ? null : reader.GetString(2),
                    NodeType = reader.IsDBNull(3) ? null : reader.GetString(3),
                }, ct)
            .Should().Within(30.Seconds()).Emit();
    }

    // ── Diagnostic ───────────────────────────────────────────────────────

    [Fact(Timeout = 30000)]
    public void Diagnostic_ResolveTableByNodeType_WorksCorrectly()
    {
        // Verify the table resolution logic
        UserPartition.ResolveTableByNodeType("Thread").Should().Be("threads");
        UserPartition.ResolveTableByNodeType("ThreadMessage").Should().Be("threads");
        UserPartition.ResolveTableByNodeType("Activity").Should().Be("activities");
        UserPartition.ResolveTableByNodeType("UserActivity").Should().Be("user_activities");
        UserPartition.ResolveTableByNodeType("Comment").Should().Be("annotations");
        UserPartition.ResolveTableByNodeType("AccessAssignment").Should().Be("access");
        // Code maps to Source which maps to "code", but NodeTypeToSuffix
        // doesn't have "Code" entry — it uses path-based resolution instead
        UserPartition.ResolveTable("User/alice/Source/MyClass").Should().Be("code");

        // Verify parser extracts nodeType correctly
        var parser = new QueryParser();
        var parsed = parser.Parse("nodeType:Thread sort:LastModified-desc");
        parsed.ExtractNodeType().Should().Be("Thread");

        // Verify scope defaults for no-path query
        parsed.Scope.Should().Be(QueryScope.Exact, "no scope specified, defaults to Exact");
    }

    // ── Thread ───────────────────────────────────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task NodeType_Thread_FindsInThreadsTable()
    {
        var ct = TestContext.Current.CancellationToken;

        // Seed a thread in the threads table
        await Write(new MeshNode("hello-world", "User/alice/_Thread")
        {
            Name = "Hello World Thread",
            NodeType = "Thread",
            MainNode = "User/alice/_Thread",
        });

        // Verify data was written to the threads table
        await _schemaDs.ScalarLong("SELECT COUNT(*) FROM threads WHERE id = 'hello-world'", ct)
            .Should().Within(30.Seconds()).Be(1L, "thread should exist in threads table");

        // Query by nodeType only (no path) — must resolve to threads table
        var results = await Query("nodeType:Thread sort:LastModified-desc");

        // Raw SQL verification: data IS in threads table
        var rawThreads = await RawQuery("threads", "Thread");
        rawThreads.Should().NotBeEmpty("raw SQL confirms threads exist in threads table");

        // Raw SQL: data is NOT in mesh_nodes
        var rawMeshNodes = await RawQuery("mesh_nodes", "Thread");

        // PostgreSqlMeshQuery must find the data in threads table
        results.Should().NotBeEmpty(
            $"nodeType:Thread should find threads. Raw threads table has {rawThreads.Count} rows, " +
            $"mesh_nodes has {rawMeshNodes.Count} Thread rows, query returned {results.Count}");
    }

    [Fact(Timeout = 30000)]
    public async Task NodeType_Thread_WithDefaultPath_FindsInThreadsTable()
    {
        await Write(new MeshNode("help-me", "User/bob/_Thread")
        {
            Name = "Help Me Thread",
            NodeType = "Thread",
            MainNode = "User/bob/_Thread",
        });

        // Simulate routing fan-out: DefaultPath = "User", scope:descendants
        var results = await Query(
            "nodeType:Thread sort:LastModified-desc scope:descendants",
            defaultPath: "User");

        results.Should().NotBeEmpty("routing fan-out with DefaultPath=User should find threads");
        results.Should().Contain(n => n.Name == "Help Me Thread");
    }

    [Fact(Timeout = 30000)]
    public async Task Namespace_Thread_FindsThreads()
    {
        await Write(new MeshNode("ns-thread", "User/carol/_Thread")
        {
            Name = "Namespace Thread",
            NodeType = "Thread",
            MainNode = "User/carol/_Thread",
        });

        // Direct namespace query
        var results = await Query("namespace:User/carol/_Thread nodeType:Thread");
        results.Should().NotBeEmpty("namespace query to _Thread should find threads");
        results.Should().Contain(n => n.Name == "Namespace Thread");
    }

    // ── ThreadMessage ────────────────────────────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task NodeType_ThreadMessage_FindsInThreadsTable()
    {
        await Write(new MeshNode("msg-1", "User/alice/_Thread/hello-world")
        {
            Name = "User message",
            NodeType = "ThreadMessage",
            MainNode = "User/alice/_Thread/hello-world",
        });

        var results = await Query("nodeType:ThreadMessage scope:descendants", defaultPath: "User");
        results.Should().NotBeEmpty("nodeType:ThreadMessage should find messages in threads table");
    }

    // ── Activity ─────────────────────────────────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task NodeType_Activity_FindsInActivitiesTable()
    {
        // Seed main node + activity satellite
        await Write(new MeshNode("doc1", "User/alice")
        {
            Name = "Alice Doc",
            NodeType = "Markdown",
        });

        await Write(new MeshNode("log1", "User/alice/doc1/_Activity")
        {
            Name = "Activity Log",
            NodeType = "Activity",
            MainNode = "User/alice/doc1",
        });

        var results = await Query("nodeType:Activity scope:descendants", defaultPath: "User");
        results.Should().NotBeEmpty("nodeType:Activity should find records in activities table");
        results.Should().Contain(n => n.NodeType == "Activity");
    }

    [Fact(Timeout = 30000)]
    public async Task SourceActivity_FindsMainNodesWithActivitySatellites()
    {
        // Main node
        await Write(new MeshNode("project1", "User/alice")
        {
            Name = "Alice Project",
            NodeType = "Markdown",
        });

        // Activity satellite
        await Write(new MeshNode("act1", "User/alice/project1/_Activity")
        {
            Name = "Project Activity",
            NodeType = "Activity",
            MainNode = "User/alice/project1",
        });

        // Dashboard query: source:activity
        var results = await Query(
            "source:activity scope:descendants sort:LastModified-desc",
            defaultPath: "User");

        // source:activity returns MAIN nodes that have activity children
        var mainResults = results.Where(n => n.NodeType != "Activity").ToList();
        mainResults.Should().NotBeEmpty(
            "source:activity should return main nodes with activity satellites");
    }

    // ── Path resolution over satellite tables (atioz NotFound-storm wedge) ──

    /// <summary>
    /// Reproduces the atioz wedge: a SubscribeRequest to a satellite path
    /// <c>{owner}/_Activity/{id}</c> went through <see cref="MeshWeaver.Hosting.PathResolutionService"/>,
    /// which issues the multi-prefix resolution query
    /// <c>path:"{deepest}"|...|"{root}"</c>. Before satellite-aware routing,
    /// that lookup only scanned <c>mesh_nodes</c>, so the deepest match was the
    /// partition root with a non-empty remainder <c>_Activity/{id}</c> →
    /// RoutingGrain NACKed NotFound → the progress reader re-subscribed →
    /// continuous [ROUTE] NotFound storm → action-block saturation → wedge.
    ///
    /// The resolution query MUST find the <c>_Activity</c> row in the
    /// <c>activities</c> satellite table so the deepest match is the full
    /// satellite path (remainder == null, routes to the owning hub).
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task PathResolutionQuery_ActivitySatellite_ResolvesToFullPath()
    {
        // Seed the partition root in mesh_nodes + a compile-activity satellite
        // in the activities table — the exact shape behind Doc/.../_Activity/compile-*.
        await Write(new MeshNode("alice", "User")
        {
            Name = "Alice",
            NodeType = "User",
        });
        await Write(new MeshNode("compile-xyz", "User/alice/_Activity")
        {
            Name = "Compile",
            NodeType = "Activity",
            MainNode = "User/alice",
        });

        // Build the resolution query EXACTLY as PathResolutionService.ResolveOnce does:
        // every prefix of the requested path, quoted, deepest-first, joined by '|'.
        const string requested = "User/alice/_Activity/compile-xyz";
        var segments = requested.Split('/');
        var pathList = string.Join("|", System.Linq.Enumerable
            .Range(1, segments.Length)
            .Select(depth => "\"" + string.Join("/", segments.Take(depth)) + "\"")
            .Reverse());

        var results = await Query($"path:{pathList}");

        // The deepest match must be the full satellite path — proving the
        // resolution query reached the activities table. If only the root
        // resolved, PathResolutionService would emit Prefix=User/alice with a
        // non-empty remainder → the NotFound storm.
        var deepest = results
            .OrderByDescending(n => (n.Path ?? "").Length)
            .FirstOrDefault();
        deepest.Should().NotBeNull("the satellite path must resolve");
        deepest!.Path.Should().Be(requested,
            "the multi-prefix resolution query must find the _Activity row in the activities table, "
            + "not stop at the partition root with a dangling _Activity/{id} remainder");
    }

    // ── UserActivity ─────────────────────────────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task NodeType_UserActivity_FindsInUserActivitiesTable()
    {
        await Write(new MeshNode("User_alice_doc1", "User/alice/_UserActivity")
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
        });

        var results = await Query("nodeType:UserActivity scope:descendants", defaultPath: "User");
        results.Should().NotBeEmpty("nodeType:UserActivity should find records in user_activities table");
    }

    // ── AccessAssignment ─────────────────────────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task NodeType_AccessAssignment_FindsInAccessTable()
    {
        await Write(new MeshNode("aa1", "User/alice/_Access")
        {
            Name = "Admin role",
            NodeType = "AccessAssignment",
            MainNode = "User/alice",
        });

        var results = await Query("nodeType:AccessAssignment scope:descendants", defaultPath: "User");
        results.Should().NotBeEmpty("nodeType:AccessAssignment should find records in access table");
    }

    // ── Comment ──────────────────────────────────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task NodeType_Comment_FindsInAnnotationsTable()
    {
        await Write(new MeshNode("cmt1", "User/alice/doc1/_Comment")
        {
            Name = "Great doc!",
            NodeType = "Comment",
            MainNode = "User/alice/doc1",
        });

        var results = await Query("nodeType:Comment scope:descendants", defaultPath: "User");
        results.Should().NotBeEmpty("nodeType:Comment should find records in annotations table");
    }

    // ── TrackedChange ────────────────────────────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task NodeType_TrackedChange_FindsInAnnotationsTable()
    {
        await Write(new MeshNode("tc1", "User/alice/doc1/_Tracking")
        {
            Name = "Section updated",
            NodeType = "TrackedChange",
            MainNode = "User/alice/doc1",
        });

        var results = await Query("nodeType:TrackedChange scope:descendants", defaultPath: "User");
        results.Should().NotBeEmpty("nodeType:TrackedChange should find records in annotations table");
    }

    // ── Approval ─────────────────────────────────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task NodeType_Approval_FindsInAnnotationsTable()
    {
        await Write(new MeshNode("apr1", "User/alice/doc1/_Approval")
        {
            Name = "Approved by Bob",
            NodeType = "Approval",
            MainNode = "User/alice/doc1",
        });

        var results = await Query("nodeType:Approval scope:descendants", defaultPath: "User");
        results.Should().NotBeEmpty("nodeType:Approval should find records in annotations table");
    }

    // ── Code ─────────────────────────────────────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task NodeType_Code_ResolvesViaContentSatelliteUnion()
    {
        await Write(new MeshNode("MyClass", "User/alice/project/Source")
        {
            Name = "MyClass.cs",
            NodeType = "Code",
            MainNode = "User/alice/project",
        });

        // Code uses Source/Test path-based resolution (no NodeTypeToSuffix entry).
        // Path-based query works:
        var byPath = await Query("namespace:User/alice/project/Source nodeType:Code");
        byPath.Should().NotBeEmpty("path-based query to Source should find Code nodes");

        // A nodeType-only query resolves to mesh_nodes (Code has no nodeType→table entry), but the
        // primary-table query now UNIONs the content satellites — the node_type filter matches in
        // the code table. This was the old "expected limitation": it silently returned empty.
        var byType = await Query("nodeType:Code scope:descendants", defaultPath: "User");
        byType.Should().Contain(n => n.Id == "MyClass",
            "a primary-table query unions the content-satellite code table, so nodeType:Code resolves");
    }

    // ── Content-satellite union (export-all completeness) ────────────────

    [Fact(Timeout = 30000)]
    public async Task Descendants_FromPartitionRoot_IncludeCodeNodes()
    {
        await Write(new MeshNode("readme", "User/alice/project")
        {
            Name = "Readme",
            NodeType = "Markdown",
            MainNode = "User/alice/project/readme",
        });
        await Write(new MeshNode("MyClass", "User/alice/project/Source")
        {
            Name = "MyClass.cs",
            NodeType = "Code",
            MainNode = "User/alice/project",
        });

        // The GitSync export enumerates exactly this shape (path:{space} scope:descendants). A
        // partition-rooted path has no Source segment, so it resolves to mesh_nodes — the union
        // with the content-satellite code table is what keeps Code nodes in the result. Before
        // the union, a Space exported WITHOUT any of its C# sources (observed live).
        var results = await Query("path:User/alice scope:descendants");
        results.Should().Contain(n => n.NodeType == "Code" && n.Id == "MyClass",
            "a partition-rooted descendants query must include content-satellite Code rows");
        results.Should().Contain(n => n.NodeType == "Markdown" && n.Id == "readme",
            "primary-table rows must still be returned alongside the satellite union");
    }

    [Fact(Timeout = 30000)]
    public async Task Descendants_FromPartitionRoot_StillExcludeMetadataSatellites()
    {
        await Write(new MeshNode("doc2", "User/bob/project")
        {
            Name = "Doc",
            NodeType = "Markdown",
            MainNode = "User/bob/project/doc2",
        });
        await Write(new MeshNode("t1", "User/bob/project/_Thread")
        {
            Name = "Discussion",
            NodeType = "Thread",
            MainNode = "User/bob/project",
        });

        // Metadata satellites (_Thread, _Activity, …) are governance data, not content — the
        // content-satellite union must NOT pull them into a plain descendants query. They remain
        // reachable via their own segment paths / nodeType filters.
        var results = await Query("path:User/bob scope:descendants");
        results.Should().Contain(n => n.Id == "doc2");
        results.Should().NotContain(n => n.NodeType == "Thread",
            "underscore metadata satellites stay out of content descendants queries");
    }

    [Fact(Timeout = 30000)]
    public async Task Descendants_UnionAcrossTables_RespectsSortAndLimit()
    {
        await Write(new MeshNode("Alpha", "User/carol/project/Source")
        {
            Name = "Alpha",
            NodeType = "Code",
            MainNode = "User/carol/project",
        });
        await Write(new MeshNode("Beta", "User/carol/project")
        {
            Name = "Beta",
            NodeType = "Markdown",
            MainNode = "User/carol/project/Beta",
        });

        // The union wrap re-applies the query's presentation ORDER BY / LIMIT on the OUTSIDE
        // (the DISTINCT ON dedup re-orders by identity internally) — a sorted+limited query must
        // pick the global winner ACROSS tables, not per-branch arbitrary rows.
        var results = await Query("path:User/carol scope:descendants sort:name limit:1");
        results.Should().ContainSingle();
        results[0].Name.Should().Be("Alpha", "sort:name across the union must rank the code-table row first");
    }
}
