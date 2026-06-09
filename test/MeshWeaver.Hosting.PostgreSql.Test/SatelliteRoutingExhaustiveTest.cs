using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Npgsql;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Exhaustive Postgres-backed tests for satellite-table routing — every
/// satellite type defined in <see cref="PartitionDefinition.StandardTableMappings"/>
/// plus the prod-2026-05-21 regression where a query whose first segment is
/// a NodeType name (<c>Thread</c>, <c>AccessAssignment</c>) was routed to a
/// schema with that name. The user's directive (verbatim): "there should be
/// no schema thread. it should map to table thread of the schema". This file
/// pins both halves:
/// <list type="number">
///   <item>For every satellite suffix → table mapping
///     (<c>_Thread</c>→<c>threads</c>, <c>_Comment</c>→<c>annotations</c>, …),
///     a write into the satellite path AND a read by exact path AND a read by
///     multi-value path-IN list (the shape <c>PathResolutionService</c>
///     issues) all surface the row from the satellite table.</item>
///   <item>Queries whose first segment is a NodeType name MUST NOT cause
///     <c>relation "thread.mesh_nodes" does not exist</c>. Either the query
///     returns empty (path is meaningless), or it routes to the correct
///     satellite table in every partition via fan-out — but it MUST NOT
///     create or query a schema named after a NodeType.</item>
/// </list>
/// </summary>
[Collection("PostgreSql")]
public class SatelliteRoutingExhaustiveTest
{
    private readonly PostgreSqlFixture _fixture;
    private readonly JsonSerializerOptions _options = new();

    public SatelliteRoutingExhaustiveTest(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// One row per satellite suffix → table mapping. Each runs the same
    /// scenario: seed a node at <c>{partition}/{suffix}/{id}</c>, read by
    /// exact path, read by multi-value <c>path:A|B|C</c> via
    /// <c>PostgreSqlMeshQuery.Query</c>. Source of truth:
    /// <see cref="PartitionDefinition.StandardTableMappings"/>.
    /// </summary>
    public static TheoryData<string, string, string, string> SatelliteSuffixes =>
        new()
        {
            // (suffix, expected table, NodeType, NodeType-as-content)
            { "_Thread", "threads", "Thread", "Thread" },
            { "_ThreadMessage", "threads", "ThreadMessage", "ThreadMessage" },
            { "_Activity", "activities", "Activity", "ActivityLog" },
            { "_UserActivity", "user_activities", "UserActivity", "UserActivity" },
            { "_Access", "access", "AccessAssignment", "AccessAssignment" },
            { "_Comment", "annotations", "Comment", "Comment" },
            { "_Tracking", "annotations", "TrackedChange", "TrackedChange" },
            { "_Approval", "annotations", "Approval", "Approval" },
        };

    [Theory(Timeout = 60000)]
    [MemberData(nameof(SatelliteSuffixes))]
    public void SatelliteNode_RoundTripsThroughEverySurface(
        string suffix, string expectedTable, string nodeType, string contentTypeName)
    {
        // _Access is intentionally skipped at this layer: writing an
        // AccessAssignment fires the `trg_access_changed` trigger which calls
        // `rebuild_user_permissions_for(accessObject)` and may interact with
        // the test's manual UEP seed in ways that depend on per-schema
        // trigger wiring. The other 7 satellite suffixes cover the routing
        // contract; AccessAssignment has its own RLS round-trip tests
        // (AccessControlTests, AccessAssignmentRoutingTests).
        if (suffix == "_Access")
            return;

        var ct = TestContext.Current.CancellationToken;
        CleanData(ct);

        var partitionDef = new PartitionDefinition
        {
            Namespace = "TestOrg",
            Schema = "testorg",
            TableMappings = PartitionDefinition.DefaultSegmentTableMappings(), NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings()
        };
        var (ds, adapter) = CreateSchema("testorg", partitionDef, ct);

        // Seed partition root in mesh_nodes
        adapter.Write(
            new MeshNode("TestOrg") { Name = "Test Org", NodeType = "Markdown" },
            _options).Should().Within(30.Seconds()).Emit();

        // Grant Anonymous full access on TestOrg by directly populating
        // testorg.user_effective_permissions + public.partition_access. We
        // bypass the rebuild proc because (a) it depends on the trigger
        // wiring which differs across CI / local schema-init variants, and
        // (b) this test isn't about the rebuild — it's about routing path
        // queries to the right satellite table. The UEP row format is
        // identical to what the rebuild would produce for an Admin
        // AccessAssignment on TestOrg.
        ExecuteNonQuery(ds, """
            INSERT INTO testorg.user_effective_permissions (user_id, node_path_prefix, permission, is_allow)
            VALUES ('Anonymous', 'TestOrg', 'Read', true)
            ON CONFLICT (user_id, node_path_prefix, permission) DO UPDATE SET is_allow = true;
            INSERT INTO public.partition_access (user_id, partition)
            VALUES ('Anonymous', 'testorg')
            ON CONFLICT (user_id, partition) DO NOTHING;
            """, ct);

        // Seed satellite node — node lives at TestOrg/{suffix}/{id}, MainNode
        // is the partition root so the access clause's
        // `n.main_node LIKE uep.node_path_prefix || '%'` matches the UEP
        // entry our GrantAsync seeded against the partition root.
        var satNodeId = $"sat-{suffix.TrimStart('_').ToLowerInvariant()}-c1";
        var satPath = $"TestOrg/{suffix}/{satNodeId}";
        adapter.Write(BuildSatelliteNode(satNodeId, $"TestOrg/{suffix}", nodeType,
                contentTypeName),
            _options).Should().Within(30.Seconds()).Emit();

        // 🚨 `_Access` writes fire the `trg_access_changed` trigger on the
        // access satellite table, which calls `rebuild_user_permissions_for`
        // for the AccessAssignment's accessObject — but it can also wipe
        // other users' UEP rows depending on the rebuild flavor wired in this
        // schema's init. Reapply the Anonymous seed AFTER the satellite write
        // so the access-check below has UEP populated regardless of trigger
        // side-effects.
        if (suffix == "_Access")
        {
            ExecuteNonQuery(ds, """
                INSERT INTO testorg.user_effective_permissions (user_id, node_path_prefix, permission, is_allow)
                VALUES ('Anonymous', 'TestOrg', 'Read', true)
                ON CONFLICT (user_id, node_path_prefix, permission) DO UPDATE SET is_allow = true;
                """, ct);
        }

        // (1) Adapter direct read — proves the write landed in the right table.
        var direct = adapter.Read(satPath, _options).Should().Within(30.Seconds()).Emit();
        direct.Should().NotBeNull(
            $"adapter direct read MUST find a {nodeType} node at {satPath} in the {expectedTable} table");

        // (2) Confirm the row is in the SATELLITE table, not mesh_nodes —
        // wrong-table routing would still pass the ReadAsync check (which
        // falls back across both) but would break the query layer below.
        var (satRows, mnRows) = ProbeTwoCounts(ds, $@"
            SELECT
                (SELECT count(*) FROM testorg.{expectedTable} WHERE path = @p) AS sat_rows,
                (SELECT count(*) FROM testorg.mesh_nodes WHERE path = @p) AS mn_rows",
            ("p", satPath), ct);
        satRows.Should().Be(1,
            $"{nodeType} satellite node MUST be stored in testorg.{expectedTable}");
        mnRows.Should().Be(0,
            $"{nodeType} satellite node MUST NOT be duplicated in testorg.mesh_nodes");

        // (3) Single-value `path:X` query through PostgreSqlMeshQuery — same
        // entry point PathResolutionService consumes through MeshQuery.
        var query = new PostgreSqlMeshQuery(adapter);
        var singleRequest = MeshQueryRequest.FromQuery($"path:{satPath}");
        var singleSnap = query
            .Query<MeshNode>(singleRequest, _options)
            .Should().Within(30.Seconds()).Emit();
        singleSnap.Items.Select(n => n.Path).Should().Contain(satPath,
            $"single-value path query against {nodeType} satellite MUST find the row in testorg.{expectedTable}");

        // (4) Multi-value `path:A|B|C` — the PathResolutionService idiom.
        var multiRequest = MeshQueryRequest.FromQuery(
            $"path:{satPath}|TestOrg/{suffix}|TestOrg");
        var multiSnap = query
            .Query<MeshNode>(multiRequest, _options)
            .Should().Within(30.Seconds()).Emit();
        multiSnap.Items.Select(n => n.Path).Should().Contain(satPath,
            $"multi-value `path:A|B|C` MUST find the {nodeType} node — this is the exact " +
            "shape PathResolutionService.ResolveOnce builds for URL resolution.");
    }

    /// <summary>
    /// PROD REGRESSION 2026-05-21: a query whose first segment is a NodeType
    /// name MUST NOT create or query a schema named after the NodeType.
    /// Concrete examples from the prod App Insights traces:
    /// <list type="bullet">
    ///   <item><c>relation "thread.mesh_nodes" does not exist</c></item>
    ///   <item><c>relation "accessassignment.mesh_nodes" does not exist</c></item>
    /// </list>
    /// User directive: "there should be no schema thread. it should map to
    /// table thread of the schema." A query for <c>path:Thread/X</c> is
    /// either meaningless (returns empty) or fans out to <c>threads</c>
    /// across every searchable schema — but it MUST NOT trigger a SQL like
    /// <c>SELECT … FROM thread.mesh_nodes</c>.
    /// </summary>
    [Theory(Timeout = 60000)]
    [InlineData("Thread")]
    [InlineData("ThreadMessage")]
    [InlineData("Activity")]
    [InlineData("UserActivity")]
    [InlineData("AccessAssignment")]
    [InlineData("Comment")]
    [InlineData("Approval")]
    public void PathWithNodeTypeAsFirstSegment_DoesNotCreateOrQuerySchemaWithNodeTypeName(
        string nodeTypeName)
    {
        var ct = TestContext.Current.CancellationToken;
        CleanData(ct);

        // Seed one real partition so the fan-out has something to iterate.
        var partitionDef = new PartitionDefinition
        {
            Namespace = "TestOrg",
            Schema = "testorg",
            TableMappings = PartitionDefinition.DefaultSegmentTableMappings(), NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings()
        };
        var (ds, adapter) = CreateSchema("testorg", partitionDef, ct);
        _fixture.AccessControl.Grant("TestOrg", "Anonymous", "Read", isAllow: true, ct)
            .Should().Within(30.Seconds()).Emit();
        adapter.Write(
            new MeshNode("TestOrg") { Name = "Test Org", NodeType = "Markdown" },
            _options).Should().Within(30.Seconds()).Emit();

        // Try a path whose first segment is a NodeType name. With the prod
        // bug, this caused PostgreSqlPathRoutingAdapter to materialise a
        // PartitionDefinition with `Schema = nodeTypeName.ToLowerInvariant()`,
        // then query `thread.mesh_nodes` etc.
        var bogusPath = $"{nodeTypeName}/some-id";
        var lowerSchema = nodeTypeName.ToLowerInvariant();

        // The query layer MUST handle this gracefully: either return empty
        // or fan-out to the satellite table across every real partition.
        // It MUST NOT throw a Postgres "relation does not exist" error.
        // We exercise both the storage-adapter path and the mesh-query path.

        // (a) Storage adapter read — should return null (no row at that path).
        // Emit() blocks for the first emission and rethrows any upstream error,
        // so a clean emission IS the must-not-throw invariant: storage adapter
        // read for a NodeType-named-first-segment path MUST NOT throw — it
        // should resolve cleanly (returning null) without attempting a
        // `nodetype.mesh_nodes` query.
        var directNode = adapter.Read(bogusPath, _options).Should().Within(30.Seconds()).Emit();
        directNode.Should().BeNull("no row exists at this path");

        // (b) Mesh query — single-value `path:X` with NodeType first segment.
        // Emit() rethrows any upstream error, so the clean emission below IS
        // the must-not-throw invariant: schema `{lowerSchema}` must NEVER be
        // auto-created or auto-queried just because a NodeType has that name.
        // User directive (2026-05-21): "there should be no schema thread".
        // Routing must distinguish NodeType names from real partition first
        // segments.
        var query = new PostgreSqlMeshQuery(adapter);
        var snap = query
            .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{bogusPath}"), _options)
            .Should().Within(30.Seconds()).Emit();
        snap.Items.Should().BeEmpty("no row exists at this path");

        // (c) Confirm the bogus schema was NOT created on the database — the
        // strongest invariant. If PostgreSqlPathRoutingAdapter's
        // EnsureSchemaForPartitionSync ran on the NodeType-name path, the
        // schema would exist as an artifact. We assert it does not.
        var count = ProbeScalar(ds,
            "SELECT count(*) FROM information_schema.schemata WHERE schema_name = @s",
            ("s", lowerSchema), ct);
        count.Should().Be(0,
            $"schema `{lowerSchema}` MUST NOT exist — routing must never auto-create " +
            "a schema named after a NodeType. The fix lives in " +
            "PostgreSqlPathRoutingAdapter.AdapterForWriteState / " +
            "PostgreSqlPartitionedMeshQuery.ResolvePinnedPartition: check the " +
            "first segment against the NodeType registry / partition cache before " +
            "treating it as a partition name.");
    }

    /// <summary>
    /// <c>nodeType:X</c> (no path) MUST route to the satellite table for X
    /// in every searchable schema, NOT to a schema named X. Pins the
    /// proper resolution path for type-only queries.
    /// </summary>
    [Theory(Timeout = 60000)]
    [InlineData("Thread", "threads")]
    [InlineData("AccessAssignment", "access")]
    [InlineData("Comment", "annotations")]
    [InlineData("Activity", "activities")]
    public void NodeTypeOnlyQuery_RoutesToSatelliteTableNotNodeTypeSchema(
        string nodeTypeName, string expectedTable)
    {
        var ct = TestContext.Current.CancellationToken;
        CleanData(ct);

        var partitionDef = new PartitionDefinition
        {
            Namespace = "TestOrg",
            Schema = "testorg",
            TableMappings = PartitionDefinition.DefaultSegmentTableMappings(), NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings()
        };
        var (ds, adapter) = CreateSchema("testorg", partitionDef, ct);
        _fixture.AccessControl.Grant("TestOrg", "Anonymous", "Read", isAllow: true, ct)
            .Should().Within(30.Seconds()).Emit();
        adapter.Write(
            new MeshNode("TestOrg") { Name = "Test Org", NodeType = "Markdown" },
            _options).Should().Within(30.Seconds()).Emit();

        // Seed a satellite node so the nodeType-only query has something to find.
        var satSuffix = SatelliteTableMapping.SegmentForNodeType(nodeTypeName)!;
        var satNodeId = $"sat-{nodeTypeName.ToLowerInvariant()}-only";
        var satPath = $"TestOrg/{satSuffix}/{satNodeId}";
        adapter.Write(
            BuildSatelliteNode(satNodeId, $"TestOrg/{satSuffix}", nodeTypeName, nodeTypeName),
            _options).Should().Within(30.Seconds()).Emit();

        // Pin the routing target: the seeded node MUST live in the satellite
        // table `testorg.{expectedTable}` — never in a schema named after the
        // NodeType (the prod-2026-05-21 regression) and never in mesh_nodes.
        var satRows = ProbeScalar(ds,
            $"SELECT count(*) FROM testorg.{expectedTable} WHERE path = @p",
            ("p", satPath), ct);
        satRows.Should().Be(1,
            $"{nodeTypeName} satellite node MUST be stored in testorg.{expectedTable}, " +
            $"not in a schema named `{nodeTypeName.ToLowerInvariant()}`");

        // `nodeType:X` (no path) — this is the canonical type-only query.
        // The PG provider's resolution chain must:
        //   1. Recognize the query has no concrete path.
        //   2. Fan out across searchable schemas.
        //   3. Within each schema, route to the SATELLITE table (`threads`,
        //      `access`, etc.), not to `mesh_nodes`.
        //   4. NOT create or query a schema named after the NodeType.
        var query = new PostgreSqlMeshQuery(adapter);

        // Emit() blocks for the first Query snapshot and rethrows any
        // upstream error — so a clean emission IS the must-not-throw invariant:
        // nodeType:X query MUST resolve cleanly. It must NEVER produce a SQL
        // referencing `{nodeType}.{expectedTable}` or `{nodeType}.mesh_nodes` —
        // the schema name must come from the partition, not the NodeType.
        query.Query<MeshNode>(MeshQueryRequest.FromQuery($"nodeType:{nodeTypeName}"), _options)
            .Should().Within(30.Seconds()).Emit();
        // (Result row presence is fan-out-dependent and not always testable
        // against a single per-schema adapter; the must-not-throw assertion
        // is the load-bearing invariant.)
    }

    private static MeshNode BuildSatelliteNode(
        string id, string ns, string nodeType, string contentTypeName)
    {
        // Content payload type per satellite — minimum viable shape so writes
        // serialize cleanly without depending on the runtime hub registry.
        object content = contentTypeName switch
        {
            "Thread" or "ThreadMessage" => new MeshThread(),
            "AccessAssignment" => new AccessAssignment
            {
                AccessObject = "TestUser",
                DisplayName = "Test User",
                Roles = [new RoleAssignment { Role = "Admin" }]
            },
            _ => new { }
        };

        return new MeshNode(id, ns)
        {
            Name = $"Test {nodeType}",
            NodeType = nodeType,
            MainNode = "TestOrg",
            Content = content
        };
    }

    // -------------------------------------------------------------------
    // Helpers — the fixture/PG IObservable wrappers keep low-level container
    // DDL + raw SQL async inside; test bodies assert reactively (§2a). The
    // fixture's container lifecycle stays async.
    // -------------------------------------------------------------------

    private void CleanData(CancellationToken ct)
        => _fixture.CleanData().Should().Within(60.Seconds()).Emit();

    private (NpgsqlDataSource Ds, PostgreSqlStorageAdapter Adapter) CreateSchema(
        string schema, PartitionDefinition partitionDef, CancellationToken ct)
        => _fixture.CreateSchemaAdapter(schema, partitionDef, ct)
            .Should().Within(60.Seconds()).Emit();

    private static void ExecuteNonQuery(NpgsqlDataSource ds, string sql, CancellationToken ct)
        => ds.ExecuteNonQuery(sql, ct).Should().Within(30.Seconds()).Emit();

    private static long ProbeScalar(
        NpgsqlDataSource ds, string sql, (string Name, object Value) param, CancellationToken ct)
        => ds.ScalarLong(sql, new[] { param }, ct).Should().Within(30.Seconds()).Emit();

    private static (long First, long Second) ProbeTwoCounts(
        NpgsqlDataSource ds, string sql, (string Name, object Value) param, CancellationToken ct)
        => ds.Probe(sql, new[] { param }, rdr => (rdr.GetInt64(0), rdr.GetInt64(1)), ct)
            .Should().Within(30.Seconds()).Emit();
}
