using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
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
        await _fixture.CleanDataAsync();

        var partitionDef = new PartitionDefinition
        {
            Namespace = "TestOrg",
            Schema = "testorg",
            TableMappings = PartitionDefinition.StandardTableMappings
        };

        var (ds, adapter) = await _fixture.CreateSchemaAdapterAsync("testorg", partitionDef, ct);

        // Create org root in mesh_nodes
        await adapter.WriteAsync(new MeshNode("TestOrg") { Name = "Test Org", NodeType = "Markdown" }, _options, ct);

        // Create a thread in threads table (path contains _Thread)
        var threadNode = new MeshNode("my-thread", "TestOrg/_Thread")
        {
            Name = "Test Thread",
            NodeType = "Thread",
            MainNode = "TestOrg",
            Content = new MeshThread { CreatedBy = "testuser" }
        };
        await adapter.WriteAsync(threadNode, _options, ct);

        // Verify thread is readable by path
        var found = await adapter.ReadAsync("TestOrg/_Thread/my-thread", _options, ct);
        found.Should().NotBeNull("thread should be found in threads table");
        found!.Name.Should().Be("Test Thread");
    }

    [Fact(Timeout = 60000)]
    public async Task ThreadMessage_UnderThread_FoundByGetNodeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanDataAsync();

        var partitionDef = new PartitionDefinition
        {
            Namespace = "TestOrg",
            Schema = "testorg",
            TableMappings = PartitionDefinition.StandardTableMappings
        };

        var (ds, adapter) = await _fixture.CreateSchemaAdapterAsync("testorg", partitionDef, ct);

        await adapter.WriteAsync(new MeshNode("TestOrg") { Name = "Test Org", NodeType = "Markdown" }, _options, ct);

        // Thread
        await adapter.WriteAsync(new MeshNode("my-thread", "TestOrg/_Thread")
        {
            Name = "Test Thread", NodeType = "Thread", MainNode = "TestOrg",
            Content = new MeshThread()
        }, _options, ct);

        // ThreadMessage under thread
        await adapter.WriteAsync(new MeshNode("msg1", "TestOrg/_Thread/my-thread")
        {
            Name = "Message 1", NodeType = "ThreadMessage", MainNode = "TestOrg",
            Content = new MeshWeaver.AI.ThreadMessage { Role = "user", Text = "Hello" }
        }, _options, ct);

        // Verify message is found
        var found = await adapter.ReadAsync("TestOrg/_Thread/my-thread/msg1", _options, ct);
        found.Should().NotBeNull("ThreadMessage should be found in threads table");
        found!.NodeType.Should().Be("ThreadMessage");
    }

    [Fact(Timeout = 60000)]
    public async Task SubThread_DeeplyNested_FoundByGetNodeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanDataAsync();

        var partitionDef = new PartitionDefinition
        {
            Namespace = "TestOrg",
            Schema = "testorg",
            TableMappings = PartitionDefinition.StandardTableMappings
        };

        var (ds, adapter) = await _fixture.CreateSchemaAdapterAsync("testorg", partitionDef, ct);

        await adapter.WriteAsync(new MeshNode("TestOrg") { Name = "Test Org", NodeType = "Markdown" }, _options, ct);

        // Thread â†’ Message â†’ Sub-thread (delegation pattern)
        await adapter.WriteAsync(new MeshNode("parent-thread", "TestOrg/_Thread")
        {
            Name = "Parent Thread", NodeType = "Thread", MainNode = "TestOrg",
            Content = new MeshThread()
        }, _options, ct);

        await adapter.WriteAsync(new MeshNode("msg1", "TestOrg/_Thread/parent-thread")
        {
            Name = "Response", NodeType = "ThreadMessage", MainNode = "TestOrg",
            Content = new MeshWeaver.AI.ThreadMessage { Role = "assistant", Text = "..." }
        }, _options, ct);

        // Sub-thread: 6 segments deep
        var subThreadPath = "TestOrg/_Thread/parent-thread/msg1/sub-thread";
        await adapter.WriteAsync(new MeshNode("sub-thread", "TestOrg/_Thread/parent-thread/msg1")
        {
            Name = "Sub Thread", NodeType = "Thread", MainNode = "TestOrg",
            Content = new MeshThread()
        }, _options, ct);

        // Verify sub-thread is found (must resolve to threads table via _Thread in path)
        var found = await adapter.ReadAsync(subThreadPath, _options, ct);
        found.Should().NotBeNull("sub-thread should be found in threads table via _Thread path segment");
        found!.Name.Should().Be("Sub Thread");
        found.NodeType.Should().Be("Thread");
    }

    [Fact(Timeout = 60000)]
    public async Task SubThread_FoundByFindBestPrefixMatchAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanDataAsync();

        var partitionDef = new PartitionDefinition
        {
            Namespace = "TestOrg",
            Schema = "testorg",
            TableMappings = PartitionDefinition.StandardTableMappings
        };

        var (ds, adapter) = await _fixture.CreateSchemaAdapterAsync("testorg", partitionDef, ct);

        await adapter.WriteAsync(new MeshNode("TestOrg") { Name = "Test Org", NodeType = "Markdown" }, _options, ct);

        await adapter.WriteAsync(new MeshNode("parent-thread", "TestOrg/_Thread")
        {
            Name = "Parent Thread", NodeType = "Thread", MainNode = "TestOrg",
            Content = new MeshThread()
        }, _options, ct);

        await adapter.WriteAsync(new MeshNode("msg1", "TestOrg/_Thread/parent-thread")
        {
            Name = "Response", NodeType = "ThreadMessage", MainNode = "TestOrg",
            Content = new MeshWeaver.AI.ThreadMessage { Role = "assistant", Text = "..." }
        }, _options, ct);

        await adapter.WriteAsync(new MeshNode("sub-thread", "TestOrg/_Thread/parent-thread/msg1")
        {
            Name = "Sub Thread", NodeType = "Thread", MainNode = "TestOrg",
            Content = new MeshThread()
        }, _options, ct);

        // FindBestPrefixMatch for the sub-thread path â€” must find it in threads table
        var (match, segments) = await adapter.FindBestPrefixMatchAsync(
            "TestOrg/_Thread/parent-thread/msg1/sub-thread", _options, ct);

        match.Should().NotBeNull("FindBestPrefixMatch should find sub-thread in threads table");
        match!.Path.Should().Be("TestOrg/_Thread/parent-thread/msg1/sub-thread");
        segments.Should().Be(5, "all 5 segments should match");
    }

    [Fact(Timeout = 60000)]
    public async Task SubThread_FoundByFindBestPrefixMatch_ForDeeperPath()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanDataAsync();

        var partitionDef = new PartitionDefinition
        {
            Namespace = "TestOrg",
            Schema = "testorg",
            TableMappings = PartitionDefinition.StandardTableMappings
        };

        var (ds, adapter) = await _fixture.CreateSchemaAdapterAsync("testorg", partitionDef, ct);

        await adapter.WriteAsync(new MeshNode("TestOrg") { Name = "Test Org", NodeType = "Markdown" }, _options, ct);

        await adapter.WriteAsync(new MeshNode("parent-thread", "TestOrg/_Thread")
        {
            Name = "Parent Thread", NodeType = "Thread", MainNode = "TestOrg",
            Content = new MeshThread()
        }, _options, ct);

        await adapter.WriteAsync(new MeshNode("msg1", "TestOrg/_Thread/parent-thread")
        {
            Name = "Response", NodeType = "ThreadMessage", MainNode = "TestOrg",
            Content = new MeshWeaver.AI.ThreadMessage { Role = "assistant", Text = "..." }
        }, _options, ct);

        await adapter.WriteAsync(new MeshNode("sub-thread", "TestOrg/_Thread/parent-thread/msg1")
        {
            Name = "Sub Thread", NodeType = "Thread", MainNode = "TestOrg",
            Content = new MeshThread()
        }, _options, ct);

        // Ask for a path DEEPER than the sub-thread (e.g., a message in the sub-thread)
        // FindBestPrefixMatch should find the sub-thread as the deepest match
        var (match, segments) = await adapter.FindBestPrefixMatchAsync(
            "TestOrg/_Thread/parent-thread/msg1/sub-thread/sub-msg1", _options, ct);

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
    /// (<c>IMeshQueryCore.ObserveQuery</c>) that <c>PathResolutionService</c>
    /// uses. The resolver builds a multi-value path query
    /// (<c>path:{full}|{ancestor1}|{ancestor2}</c>) and expects at least one
    /// exact match for the deepest path that exists.
    ///
    /// <para>This test pins that path-shape end-to-end against
    /// <see cref="PostgreSqlMeshQuery.ObserveQuery"/> (the same provider the
    /// resolver consumes). If it goes red, the production satellite-path
    /// resolution is broken; if it stays green, the resolver-level bug lies
    /// elsewhere (e.g. multi-provider aggregation, access-control filter).</para>
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task PathQuery_MultiValuePathListIncludingSatellitePath_ReturnsThreadRow()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanDataAsync();

        var partitionDef = new PartitionDefinition
        {
            Namespace = "TestOrg",
            Schema = "testorg",
            TableMappings = PartitionDefinition.StandardTableMappings
        };

        var (ds, adapter) = await _fixture.CreateSchemaAdapterAsync("testorg", partitionDef, ct);

        // Grant Anonymous Read so the ObserveQuery isn't filtered out by RLS —
        // the prod bug exists independent of access control; we want to isolate
        // the path-resolution surface.
        await _fixture.AccessControl.GrantAsync("TestOrg", "Anonymous", "Read", isAllow: true, ct);

        // Seed: partition root + thread in _Thread satellite. This is the exact
        // shape of the prod row (verified via MCP) — the difference is that
        // the prod data is in `systemorph.threads`, here it's `testorg.threads`.
        await adapter.WriteAsync(new MeshNode("TestOrg")
        {
            Name = "Test Org",
            NodeType = "Markdown"
        }, _options, ct);

        await adapter.WriteAsync(new MeshNode("add-markus-kleiner-as-admin-c578", "TestOrg/_Thread")
        {
            Name = "add markus kleiner as admin",
            NodeType = "Thread",
            MainNode = "TestOrg",
            Content = new MeshThread { CreatedBy = "rbuergi" }
        }, _options, ct);

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

        // Sanity check: adapter.ReadAsync (already known to work — existing tests
        // ThreadNode_StoredInThreadsTable_FoundByGetNodeAsync) DOES find the row.
        // That confirms the row is in the threads table with the right path.
        var direct = await adapter.ReadAsync(
            "TestOrg/_Thread/add-markus-kleiner-as-admin-c578", _options, ct);
        direct.Should().NotBeNull("adapter direct read finds the thread row");

        // Confirm the row really is in `testorg.threads` (not mesh_nodes) and
        // that partition_access is populated for Anonymous.
        await using (var probe = ds.CreateCommand(@"
            SELECT
                (SELECT count(*) FROM testorg.threads WHERE path = 'TestOrg/_Thread/add-markus-kleiner-as-admin-c578') AS thread_rows,
                (SELECT count(*) FROM testorg.mesh_nodes WHERE path = 'TestOrg/_Thread/add-markus-kleiner-as-admin-c578') AS meshnodes_rows,
                (SELECT count(*) FROM public.partition_access WHERE user_id = 'Anonymous' AND partition = 'testorg') AS partition_access_rows,
                (SELECT count(*) FROM testorg.user_effective_permissions WHERE user_id = 'Anonymous') AS uep_rows"))
        await using (var rdr = await probe.ExecuteReaderAsync(ct))
        {
            await rdr.ReadAsync(ct);
            var threadRows = rdr.GetInt64(0);
            var meshRows = rdr.GetInt64(1);
            var pa = rdr.GetInt64(2);
            var uep = rdr.GetInt64(3);
            // Surface via xUnit's test output so we see the actual data
            // shape on failure. Diagnostic only — assertions below pin the
            // behaviour.
            System.Console.WriteLine(
                $"[DIAG] thread_rows={threadRows} meshnodes_rows={meshRows} " +
                $"partition_access(Anonymous,testorg)={pa} uep(Anonymous)={uep}");
            threadRows.Should().Be(1, "thread MUST be in testorg.threads");
            meshRows.Should().Be(0, "thread must NOT be duplicated in testorg.mesh_nodes");
        }

        // Single-value path query (no IN-list) — does the satellite routing
        // work in that simpler shape? If THIS also returns empty, the bug is
        // the satellite path routing itself; if it returns the row, the bug
        // is in the multi-value path-IN-list handling.
        var singlePathRequest = MeshQueryRequest.FromQuery(
            "path:TestOrg/_Thread/add-markus-kleiner-as-admin-c578");
        var singleSnapshot = await query
            .ObserveQuery<MeshNode>(singlePathRequest, _options)
            .FirstAsync()
            .ToTask(ct);
        singleSnapshot.Items.Select(n => n.Path).Should().Contain(
            "TestOrg/_Thread/add-markus-kleiner-as-admin-c578",
            "single-value path query MUST find the thread row — this is the simplest possible " +
            "satellite-table routing test. If this fails, the bug is the satellite routing.");

        var snapshot = await query
            .ObserveQuery<MeshNode>(request, _options)
            .FirstAsync()
            .ToTask(ct);

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
            "CLAUDE.md \"Satellite table routing by path segment\")");
        threadRow!.NodeType.Should().Be("Thread");
    }
}
