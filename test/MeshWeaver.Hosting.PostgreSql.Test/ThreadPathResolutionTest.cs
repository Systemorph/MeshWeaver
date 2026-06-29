using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Npgsql;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Tests that deeply nested _Thread paths resolve correctly in PostgreSQL.
/// Thread and sub-thread nodes must be stored in the "threads" satellite table
/// and the path resolution must find them there for the entire sub-path.
///
/// Reproduces the production bug: delegation creates a sub-thread at
///   Org/_Thread/thread-id/msg-id/sub-thread-id
/// but path resolution can't find it because it looks in mesh_nodes
/// instead of threads.
/// </summary>
[Collection("PostgreSql")]
public class ThreadPathResolutionTest
{
    private readonly PostgreSqlFixture _fixture;
    private readonly JsonSerializerOptions _options = new();

    public ThreadPathResolutionTest(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Timeout = 60000)]
    public async Task ThreadNode_StoredInThreadsTable_FoundByGetNodeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await CleanData(ct);

        var partitionDef = new PartitionDefinition
        {
            Namespace = "TestOrg",
            Schema = "testorg",
            TableMappings = PartitionDefinition.DefaultSegmentTableMappings(), NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings()
        };

        var (ds, adapter) = await CreateSchema("testorg", partitionDef, ct);

        // Create org root in mesh_nodes
        await adapter.Write(new MeshNode("TestOrg") { Name = "Test Org", NodeType = "Markdown" }, _options)
            .Should().Within(30.Seconds()).Emit();

        // Create a thread in threads table (path contains _Thread)
        var threadNode = new MeshNode("my-thread", "TestOrg/_Thread")
        {
            Name = "Test Thread",
            NodeType = "Thread",
            MainNode = "TestOrg",
            Content = new MeshThread { CreatedBy = "testuser" }
        };
        await adapter.Write(threadNode, _options).Should().Within(30.Seconds()).Emit();

        // Verify thread is readable by path
        var found = await adapter.Read("TestOrg/_Thread/my-thread", _options).Should().Within(30.Seconds()).Emit();
        found.Should().NotBeNull("thread should be found in threads table");
        found!.Name.Should().Be("Test Thread");
    }

    [Fact(Timeout = 60000)]
    public async Task ThreadMessage_UnderThread_FoundByGetNodeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await CleanData(ct);

        var partitionDef = new PartitionDefinition
        {
            Namespace = "TestOrg",
            Schema = "testorg",
            TableMappings = PartitionDefinition.DefaultSegmentTableMappings(), NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings()
        };

        var (ds, adapter) = await CreateSchema("testorg", partitionDef, ct);

        await adapter.Write(new MeshNode("TestOrg") { Name = "Test Org", NodeType = "Markdown" }, _options)
            .Should().Within(30.Seconds()).Emit();

        // Thread
        await adapter.Write(new MeshNode("my-thread", "TestOrg/_Thread")
        {
            Name = "Test Thread", NodeType = "Thread", MainNode = "TestOrg",
            Content = new MeshThread()
        }, _options).Should().Within(30.Seconds()).Emit();

        // ThreadMessage under thread
        await adapter.Write(new MeshNode("msg1", "TestOrg/_Thread/my-thread")
        {
            Name = "Message 1", NodeType = "ThreadMessage", MainNode = "TestOrg",
            Content = new MeshWeaver.AI.ThreadMessage { Role = "user", Text = "Hello" }
        }, _options).Should().Within(30.Seconds()).Emit();

        // Verify message is found
        var found = await adapter.Read("TestOrg/_Thread/my-thread/msg1", _options).Should().Within(30.Seconds()).Emit();
        found.Should().NotBeNull("ThreadMessage should be found in threads table");
        found!.NodeType.Should().Be("ThreadMessage");
    }

    [Fact(Timeout = 60000)]
    public async Task SubThread_DeeplyNested_FoundByGetNodeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await CleanData(ct);

        var partitionDef = new PartitionDefinition
        {
            Namespace = "TestOrg",
            Schema = "testorg",
            TableMappings = PartitionDefinition.DefaultSegmentTableMappings(), NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings()
        };

        var (ds, adapter) = await CreateSchema("testorg", partitionDef, ct);

        await adapter.Write(new MeshNode("TestOrg") { Name = "Test Org", NodeType = "Markdown" }, _options)
            .Should().Within(30.Seconds()).Emit();

        // Thread → Message → Sub-thread (delegation pattern)
        await adapter.Write(new MeshNode("parent-thread", "TestOrg/_Thread")
        {
            Name = "Parent Thread", NodeType = "Thread", MainNode = "TestOrg",
            Content = new MeshThread()
        }, _options).Should().Within(30.Seconds()).Emit();

        await adapter.Write(new MeshNode("msg1", "TestOrg/_Thread/parent-thread")
        {
            Name = "Response", NodeType = "ThreadMessage", MainNode = "TestOrg",
            Content = new MeshWeaver.AI.ThreadMessage { Role = "assistant", Text = "..." }
        }, _options).Should().Within(30.Seconds()).Emit();

        // Sub-thread: 6 segments deep
        var subThreadPath = "TestOrg/_Thread/parent-thread/msg1/sub-thread";
        await adapter.Write(new MeshNode("sub-thread", "TestOrg/_Thread/parent-thread/msg1")
        {
            Name = "Sub Thread", NodeType = "Thread", MainNode = "TestOrg",
            Content = new MeshThread()
        }, _options).Should().Within(30.Seconds()).Emit();

        // Verify sub-thread is found (must resolve to threads table via _Thread in path)
        var found = await adapter.Read(subThreadPath, _options).Should().Within(30.Seconds()).Emit();
        found.Should().NotBeNull("sub-thread should be found in threads table via _Thread path segment");
        found!.Name.Should().Be("Sub Thread");
        found.NodeType.Should().Be("Thread");
    }

    [Fact(Timeout = 60000)]
    public async Task SubThread_FoundByFindBestPrefixMatchAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await CleanData(ct);

        var partitionDef = new PartitionDefinition
        {
            Namespace = "TestOrg",
            Schema = "testorg",
            TableMappings = PartitionDefinition.DefaultSegmentTableMappings(), NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings()
        };

        var (ds, adapter) = await CreateSchema("testorg", partitionDef, ct);

        await adapter.Write(new MeshNode("TestOrg") { Name = "Test Org", NodeType = "Markdown" }, _options)
            .Should().Within(30.Seconds()).Emit();

        await adapter.Write(new MeshNode("parent-thread", "TestOrg/_Thread")
        {
            Name = "Parent Thread", NodeType = "Thread", MainNode = "TestOrg",
            Content = new MeshThread()
        }, _options).Should().Within(30.Seconds()).Emit();

        await adapter.Write(new MeshNode("msg1", "TestOrg/_Thread/parent-thread")
        {
            Name = "Response", NodeType = "ThreadMessage", MainNode = "TestOrg",
            Content = new MeshWeaver.AI.ThreadMessage { Role = "assistant", Text = "..." }
        }, _options).Should().Within(30.Seconds()).Emit();

        await adapter.Write(new MeshNode("sub-thread", "TestOrg/_Thread/parent-thread/msg1")
        {
            Name = "Sub Thread", NodeType = "Thread", MainNode = "TestOrg",
            Content = new MeshThread()
        }, _options).Should().Within(30.Seconds()).Emit();

        // FindBestPrefixMatch for the sub-thread path — must find it in threads table
        var (match, segments) = await adapter.FindBestPrefixMatch(
            "TestOrg/_Thread/parent-thread/msg1/sub-thread", _options)
            .Should().Within(30.Seconds()).Emit();

        match.Should().NotBeNull("FindBestPrefixMatch should find sub-thread in threads table");
        match!.Path.Should().Be("TestOrg/_Thread/parent-thread/msg1/sub-thread");
        segments.Should().Be(5, "all 5 segments should match");
    }

    [Fact(Timeout = 60000)]
    public async Task SubThread_FoundByFindBestPrefixMatch_ForDeeperPath()
    {
        var ct = TestContext.Current.CancellationToken;
        await CleanData(ct);

        var partitionDef = new PartitionDefinition
        {
            Namespace = "TestOrg",
            Schema = "testorg",
            TableMappings = PartitionDefinition.DefaultSegmentTableMappings(), NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings()
        };

        var (ds, adapter) = await CreateSchema("testorg", partitionDef, ct);

        await adapter.Write(new MeshNode("TestOrg") { Name = "Test Org", NodeType = "Markdown" }, _options)
            .Should().Within(30.Seconds()).Emit();

        await adapter.Write(new MeshNode("parent-thread", "TestOrg/_Thread")
        {
            Name = "Parent Thread", NodeType = "Thread", MainNode = "TestOrg",
            Content = new MeshThread()
        }, _options).Should().Within(30.Seconds()).Emit();

        await adapter.Write(new MeshNode("msg1", "TestOrg/_Thread/parent-thread")
        {
            Name = "Response", NodeType = "ThreadMessage", MainNode = "TestOrg",
            Content = new MeshWeaver.AI.ThreadMessage { Role = "assistant", Text = "..." }
        }, _options).Should().Within(30.Seconds()).Emit();

        await adapter.Write(new MeshNode("sub-thread", "TestOrg/_Thread/parent-thread/msg1")
        {
            Name = "Sub Thread", NodeType = "Thread", MainNode = "TestOrg",
            Content = new MeshThread()
        }, _options).Should().Within(30.Seconds()).Emit();

        // Ask for a path DEEPER than the sub-thread (e.g., a message in the sub-thread)
        // FindBestPrefixMatch should find the sub-thread as the deepest match
        var (match, segments) = await adapter.FindBestPrefixMatch(
            "TestOrg/_Thread/parent-thread/msg1/sub-thread/sub-msg1", _options)
            .Should().Within(30.Seconds()).Emit();

        match.Should().NotBeNull("should find sub-thread as best prefix for deeper path");
        match!.Path.Should().Be("TestOrg/_Thread/parent-thread/msg1/sub-thread");
        segments.Should().Be(5);
    }

    /// <summary>
    /// Prod 2026-05-21 repro: a thread URL like
    /// <c>https://memex.meshweaver.cloud/Systemorph/_Thread/add-markus-kleiner-as-admin-to-systemorp-c578</c>
    /// returned "Page not found: ... does not match any registered address pattern".
    /// The thread row IS in <c>systemorph.threads</c> (verified via MCP); the
    /// adapter's direct <c>ReadAsync</c> finds it (existing
    /// <c>ThreadNode_StoredInThreadsTable_FoundByGetNodeAsync</c> proves this).
    /// The failure happens one layer up — at the query level
    /// (<c>IMeshQueryCore.Query</c>) that <c>PathResolutionService</c>
    /// uses. The resolver builds a multi-value path query
    /// (<c>path:{full}|{ancestor1}|{ancestor2}</c>) and expects at least one
    /// exact match for the deepest path that exists.
    ///
    /// <para>This test pins that path-shape end-to-end against
    /// <see cref="PostgreSqlMeshQuery.Query"/> (the same provider the
    /// resolver consumes). If it goes red, the production satellite-path
    /// resolution is broken; if it stays green, the resolver-level bug lies
    /// elsewhere (e.g. multi-provider aggregation, access-control filter).</para>
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task PathQuery_MultiValuePathListIncludingSatellitePath_ReturnsThreadRow()
    {
        var ct = TestContext.Current.CancellationToken;
        await CleanData(ct);

        var partitionDef = new PartitionDefinition
        {
            Namespace = "TestOrg",
            Schema = "testorg",
            TableMappings = PartitionDefinition.DefaultSegmentTableMappings(), NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings()
        };

        var (ds, adapter) = await CreateSchema("testorg", partitionDef, ct);

        // Grant Anonymous Read so the Query isn't filtered out by RLS —
        // the prod bug exists independent of access control; we want to isolate
        // the path-resolution surface.
        await _fixture.AccessControl.Grant("TestOrg", "Anonymous", "Read", isAllow: true, ct)
            .Should().Within(30.Seconds()).Emit();

        // Seed: partition root + thread in _Thread satellite. This is the exact
        // shape of the prod row (verified via MCP) — the difference is that
        // the prod data is in `systemorph.threads`, here it's `testorg.threads`.
        await adapter.Write(new MeshNode("TestOrg")
        {
            Name = "Test Org",
            NodeType = "Markdown"
        }, _options).Should().Within(30.Seconds()).Emit();

        await adapter.Write(new MeshNode("add-markus-kleiner-as-admin-c578", "TestOrg/_Thread")
        {
            Name = "add markus kleiner as admin",
            NodeType = "Thread",
            MainNode = "TestOrg",
            Content = new MeshThread { CreatedBy = "rbuergi" }
        }, _options).Should().Within(30.Seconds()).Emit();

        // The exact multi-value path query PathResolutionService produces for
        // URL "TestOrg/_Thread/add-markus-kleiner-as-admin-c578":
        //   segments = ["TestOrg", "_Thread", "add-markus-kleiner-as-admin-c578"]
        //   pathList = "TestOrg/_Thread/add-markus-kleiner-as-admin-c578|TestOrg/_Thread|TestOrg"
        //   request  = MeshQueryRequest.FromQuery("path:<pathList>")
        // The resolver picks the longest-matching node as the prefix; remainder
        // is whatever URL segments come after.
        var query = new PostgreSqlMeshQuery(adapter);
        var request = MeshQueryRequest.FromQuery(
            "path:TestOrg/_Thread/add-markus-kleiner-as-admin-c578|TestOrg/_Thread|TestOrg");

        // Sanity check: adapter.Read (already known to work — existing tests
        // ThreadNode_StoredInThreadsTable_FoundByGetNodeAsync) DOES find the row.
        // That confirms the row is in the threads table with the right path.
        var direct = await adapter.Read(
            "TestOrg/_Thread/add-markus-kleiner-as-admin-c578", _options)
            .Should().Within(30.Seconds()).Emit();
        direct.Should().NotBeNull("adapter direct read finds the thread row");

        // Confirm the row really is in `testorg.threads` (not mesh_nodes) and
        // that partition_access is populated for Anonymous. The raw reader probe
        // is genuine async DB I/O; bridge it at the SDK boundary so this test
        // body stays void + blocking-reactive (§2a).
        var (threadRows, meshRows, pa, uep) = await ProbeRowCounts(ds, ct);
        // Surface via xUnit's test output so we see the actual data
        // shape on failure. Diagnostic only — assertions below pin the
        // behaviour.
        System.Console.WriteLine(
            $"[DIAG] thread_rows={threadRows} meshnodes_rows={meshRows} " +
            $"partition_access(Anonymous,testorg)={pa} uep(Anonymous)={uep}");
        threadRows.Should().Be(1, "thread MUST be in testorg.threads");
        meshRows.Should().Be(0, "thread must NOT be duplicated in testorg.mesh_nodes");

        // 🚨 2026-05-21 — PathResolutionService now sets UserId = System on
        // its query requests so path resolution bypasses access control. The
        // PG provider sees System and passes null userId to QueryAcrossSchemas
        // (PostgreSqlPartitionedMeshQuery.cs:323), which OMITS the access
        // clause. The path resolves regardless of the caller's per-partition
        // grants; the owning hub then enforces Read at content-load time.
        //
        // Use the same shape here so the test pins what PathResolutionService
        // actually does. Without UserId=System the query runs as Anonymous,
        // and Anonymous needs explicit partition_access + UEP — that's a
        // separate concern (access-control mechanics) from path routing.
        var singlePathRequest = MeshQueryRequest.FromQuery(
            "path:TestOrg/_Thread/add-markus-kleiner-as-admin-c578") with
        {
            UserId = MeshWeaver.Mesh.Security.WellKnownUsers.System,
        };
        var singleSnapshot = await query
            .Query<MeshNode>(singlePathRequest, _options)
            .Should().Within(30.Seconds()).Emit();
        singleSnapshot.Items.Select(n => n.Path).Should().Contain(
            "TestOrg/_Thread/add-markus-kleiner-as-admin-c578",
            "single-value path query under System identity MUST find the thread row — " +
            "this pins the routing surface PathResolutionService uses (System bypass).");

        // Same shape applied to the multi-value path query — what
        // PathResolutionService actually emits when resolving a satellite URL.
        var systemRequest = request with
        {
            UserId = MeshWeaver.Mesh.Security.WellKnownUsers.System,
        };
        var snapshot = await query
            .Query<MeshNode>(systemRequest, _options)
            .Should().Within(30.Seconds()).Emit();

        // The Initial emission MUST include the thread row — that's what makes
        // the resolver's "best by Path.Length" pick the thread itself (and
        // therefore not return "Page not found").
        snapshot.Items.Should().NotBeEmpty(
            "multi-value path query MUST surface at least the deepest matching " +
            "row (the thread in _Thread satellite). Returning empty here is the " +
            "exact prod 2026-05-21 symptom — the resolver receives null and " +
            "NavigationStatus.NotFound is emitted to the user.");

        var threadRow = snapshot.Items.FirstOrDefault(n =>
            n.Path == "TestOrg/_Thread/add-markus-kleiner-as-admin-c578");
        threadRow.Should().NotBeNull(
            "the deepest path in the IN-list IS the thread row in `testorg.threads`; " +
            "the query MUST route to the satellite table by path segment (per " +
            "AGENTS.md \"Satellite table routing by path segment\")");
        threadRow!.NodeType.Should().Be("Thread");
    }

    /// <summary>
    /// Path-resolution coverage: a query under <c>UserId=Anonymous</c> with NO
    /// partition_access / UEP entries — the SAME query a never-authenticated
    /// browser session would emit — MUST still find a satellite path when the
    /// request is stamped with <see cref="WellKnownUsers.System"/>. This is
    /// the 2026-05-21 fix: <c>PathResolutionService</c> sets UserId=System on
    /// every resolve so URL → address mapping is access-control-free; the
    /// owning hub gates Read at content-load.
    ///
    /// <para>Without the fix this test reproduces the prod symptom — Anonymous
    /// can't read <c>systemorph.threads</c> (no partition_access for
    /// Anonymous on systemorph) and PathResolutionService emits NotFound.</para>
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task PathResolution_AsAnonymous_NoPartitionAccess_StillFindsSatelliteRow_UnderSystemBypass()
    {
        var ct = TestContext.Current.CancellationToken;
        await CleanData(ct);

        var partitionDef = new PartitionDefinition
        {
            Namespace = "PrivateOrg",
            Schema = "privateorg",
            TableMappings = PartitionDefinition.DefaultSegmentTableMappings(), NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings()
        };
        var (ds, adapter) = await CreateSchema("privateorg", partitionDef, ct);

        // Deliberately NO partition_access for Anonymous and NO UEP entry for
        // Anonymous. The schema is "private" by default — only explicitly
        // granted users can read.
        await adapter.Write(new MeshNode("PrivateOrg")
        {
            Name = "Private Org",
            NodeType = "Markdown",
        }, _options).Should().Within(30.Seconds()).Emit();
        await adapter.Write(new MeshNode("secret-thread", "PrivateOrg/_Thread")
        {
            Name = "Secret",
            NodeType = "Thread",
            MainNode = "PrivateOrg",
            Content = new MeshThread { CreatedBy = "owner" }
        }, _options).Should().Within(30.Seconds()).Emit();

        // 1. WITHOUT System bypass — Anonymous can't see the row (no access).
        //    This is the prod symptom: query returns empty → NotFound.
        var query = new PostgreSqlMeshQuery(adapter);
        var anonRequest = MeshQueryRequest.FromQuery(
            "path:PrivateOrg/_Thread/secret-thread");
        var anonSnapshot = await query
            .Query<MeshNode>(anonRequest, _options)
            .Should().Within(30.Seconds()).Emit();
        anonSnapshot.Items.Should().BeEmpty(
            "Anonymous user without partition_access SHOULD be denied at the access " +
            "clause — that's the security contract. Path resolution must bypass this.");

        // 2. WITH System bypass — the row is found regardless of access.
        //    This is what PathResolutionService now does so users see "Page found"
        //    + content-load denial rather than "Page not found" + URL-is-wrong.
        var systemRequest = anonRequest with
        {
            UserId = MeshWeaver.Mesh.Security.WellKnownUsers.System,
        };
        var systemSnapshot = await query
            .Query<MeshNode>(systemRequest, _options)
            .Should().Within(30.Seconds()).Emit();
        systemSnapshot.Items.Should().ContainSingle(n =>
            n.Path == "PrivateOrg/_Thread/secret-thread",
            "System bypass MUST find the satellite row even when no user has " +
            "explicit access — path resolution is routing, not data access. " +
            "The owning hub enforces Read at content-load time (PathResolutionService " +
            "intentionally sets UserId=System on its query requests).");
    }

    /// <summary>
    /// Pinning the partition from the path's first segment — for a path like
    /// <c>Systemorph/_Thread/X</c>, the resolver knows the partition is
    /// <c>systemorph</c> and there's no reason to fan out across every
    /// searchable schema. This test pins the no-fan-out invariant: queries for
    /// a satellite path under a specific partition MUST be answered by that
    /// partition's tables only.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task PathResolution_SatelliteUnderPartition_OnlyHitsThatPartition()
    {
        var ct = TestContext.Current.CancellationToken;
        await CleanData(ct);

        // Create TWO partitions, each with a thread row at the same satellite
        // suffix. If the query fans out, both would surface; if it pins to the
        // requested partition, only one surfaces.
        var defA = new PartitionDefinition
        {
            Namespace = "OrgA", Schema = "orga",
            TableMappings = PartitionDefinition.DefaultSegmentTableMappings(), NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings()
        };
        var defB = new PartitionDefinition
        {
            Namespace = "OrgB", Schema = "orgb",
            TableMappings = PartitionDefinition.DefaultSegmentTableMappings(), NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings()
        };
        var (dsA, adapterA) = await CreateSchema("orga", defA, ct);
        var (dsB, adapterB) = await CreateSchema("orgb", defB, ct);

        await adapterA.Write(new MeshNode("OrgA") { NodeType = "Markdown" }, _options)
            .Should().Within(30.Seconds()).Emit();
        await adapterA.Write(new MeshNode("t1", "OrgA/_Thread")
        {
            NodeType = "Thread", MainNode = "OrgA",
            Content = new MeshThread { CreatedBy = "u" }
        }, _options).Should().Within(30.Seconds()).Emit();
        await adapterB.Write(new MeshNode("OrgB") { NodeType = "Markdown" }, _options)
            .Should().Within(30.Seconds()).Emit();
        await adapterB.Write(new MeshNode("t1", "OrgB/_Thread")
        {
            NodeType = "Thread", MainNode = "OrgB",
            Content = new MeshThread { CreatedBy = "u" }
        }, _options).Should().Within(30.Seconds()).Emit();

        // Query for the OrgA path — should hit OrgA only, not OrgB.
        // Even with System bypass (the production path) the partition pin
        // narrows the fan-out via ResolvePinnedPartition (first segment of
        // path) — so only `orga.threads` is queried.
        var query = new PostgreSqlMeshQuery(adapterA);
        var request = MeshQueryRequest.FromQuery("path:OrgA/_Thread/t1") with
        {
            UserId = MeshWeaver.Mesh.Security.WellKnownUsers.System,
        };
        var snapshot = await query
            .Query<MeshNode>(request, _options)
            .Should().Within(30.Seconds()).Emit();

        // Exactly one row, from OrgA. If we got OrgB's t1 too, the fan-out
        // is over-broad — wasted cycles AND wrong (the resolver would treat
        // a sibling partition's match as a candidate).
        snapshot.Items.Select(n => n.Path).Should().ContainSingle()
            .Which.Should().Be("OrgA/_Thread/t1",
                "path's first segment IS the partition — the satellite query MUST " +
                "scope to that partition only. PathResolutionService never resolves " +
                "across partitions; the path always carries its partition prefix.");
    }

    // -------------------------------------------------------------------
    // Helpers — the fixture's IObservable forms keep low-level container DDL
    // async inside; test bodies assert reactively (§2a). The fixture's own
    // InitializeAsync/DisposeAsync container lifecycle stays async.
    // -------------------------------------------------------------------

    private Task CleanData(CancellationToken ct)
        => _fixture.CleanData().Should().Within(60.Seconds()).Emit();

    private Task<(NpgsqlDataSource Ds, PostgreSqlStorageAdapter Adapter)> CreateSchema(
        string schema, PartitionDefinition partitionDef, CancellationToken ct)
        => _fixture.CreateSchemaAdapter(schema, partitionDef, ct)
            .Should().Within(60.Seconds()).Emit();

    private static Task<(long ThreadRows, long MeshRows, long PartitionAccess, long Uep)>
        ProbeRowCounts(NpgsqlDataSource ds, CancellationToken ct)
        => ds.Probe(@"
            SELECT
                (SELECT count(*) FROM testorg.threads WHERE path = 'TestOrg/_Thread/add-markus-kleiner-as-admin-c578') AS thread_rows,
                (SELECT count(*) FROM testorg.mesh_nodes WHERE path = 'TestOrg/_Thread/add-markus-kleiner-as-admin-c578') AS meshnodes_rows,
                (SELECT count(*) FROM public.partition_access WHERE user_id = 'Anonymous' AND partition = 'testorg') AS partition_access_rows,
                (SELECT count(*) FROM testorg.user_effective_permissions WHERE user_id = 'Anonymous') AS uep_rows",
            System.Array.Empty<(string, object)>(),
            rdr => (rdr.GetInt64(0), rdr.GetInt64(1), rdr.GetInt64(2), rdr.GetInt64(3)), ct)
            .Should().Within(30.Seconds()).Emit();
}
