using MeshWeaver.Hosting.PostgreSql;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Unit tests for the table + schema resolution rules in
/// <see cref="PostgreSqlPartitionedMeshQuery"/>. These rules drive the
/// cross-partition fan-out from <c>UserActivityLayoutAreas</c> and any
/// other consumer of the partitioned Postgres provider — getting them
/// wrong produces empty Latest Threads / Activity Feed / Recently Viewed
/// dashboards in prod with no error.
///
/// <para>The contract (per the user's mapping spec):</para>
/// <list type="bullet">
///   <item><c>namespace:*/_Thread</c> → table <c>threads</c>, fan out across
///     ALL partitions (no pinned schema).</item>
///   <item><c>nodeType:Thread</c> → table <c>threads</c>.</item>
///   <item><c>nodeType:ThreadMessage</c> → table <c>threads</c> (Thread +
///     ThreadMessage share the threads table per
///     <c>PartitionDefinition.StandardTableMappings</c>).</item>
///   <item><c>namespace:partition/*/_Thread</c> → table <c>threads</c>,
///     fan-out narrowed to schema <c>partition</c>.</item>
///   <item><c>source:activity</c> + any scope → primary table
///     <c>mesh_nodes</c> joined with <c>activities</c> (handled separately
///     in EnumerateFanOutAsync; the table resolver itself returns
///     mesh_nodes since source:activity projects from the main node).</item>
///   <item>Non-satellite paths and queries with no satellite hint fall back
///     to <c>mesh_nodes</c>.</item>
/// </list>
/// </summary>
public class PostgreSqlPartitionedMeshQueryMappingTests
{
    private readonly QueryParser _parser = new();

    // ─── ResolveTable ──────────────────────────────────────────────────

    [Theory]
    [InlineData("namespace:*/_Thread", "threads")]
    [InlineData("namespace:*/_ThreadMessage", "threads")]
    [InlineData("namespace:partition/*/_Thread", "threads")]
    [InlineData("namespace:partition/doc/_Thread", "threads")]
    [InlineData("namespace:partition/doc/_Comment", "annotations")]
    [InlineData("namespace:partition/doc/_Approval", "annotations")]
    [InlineData("namespace:partition/doc/_Tracking", "annotations")]
    [InlineData("namespace:partition/doc/_Activity", "activities")]
    [InlineData("namespace:partition/doc/_UserActivity", "user_activities")]
    [InlineData("namespace:partition/_Access", "access")]
    [InlineData("namespace:partition/Source/code", "code")]
    [InlineData("namespace:partition/Test/code", "code")]
    public void ResolveTable_FromPathSatelliteSegment(string query, string expectedTable)
    {
        var parsed = _parser.Parse(query);
        var table = PostgreSqlPartitionedMeshQuery.ResolveTable(parsed);
        table.Should().Be(expectedTable);
    }

    [Theory]
    [InlineData("nodeType:Thread", "threads")]
    [InlineData("nodeType:ThreadMessage", "threads")]
    [InlineData("nodeType:Activity", "activities")]
    [InlineData("nodeType:UserActivity", "user_activities")]
    [InlineData("nodeType:Comment", "annotations")]
    [InlineData("nodeType:Approval", "annotations")]
    [InlineData("nodeType:TrackedChange", "annotations")]
    [InlineData("nodeType:AccessAssignment", "access")]
    public void ResolveTable_FromNodeTypeFilter(string query, string expectedTable)
    {
        var parsed = _parser.Parse(query);
        var table = PostgreSqlPartitionedMeshQuery.ResolveTable(parsed);
        table.Should().Be(expectedTable);
    }

    [Theory]
    [InlineData("")]
    [InlineData("namespace:partition")]
    [InlineData("namespace:partition/doc")]
    [InlineData("nodeType:Markdown")]
    [InlineData("nodeType:Space")]
    public void ResolveTable_FallsBackToMeshNodes_WhenNoSatelliteHint(string query)
    {
        var parsed = _parser.Parse(query);
        var table = PostgreSqlPartitionedMeshQuery.ResolveTable(parsed);
        table.Should().Be("mesh_nodes");
    }

    [Theory]
    [InlineData("namespace:*/_ThreadMessage nodeType:Thread", "threads")] // both → still threads
    [InlineData("namespace:*/_Activity nodeType:Activity", "activities")]
    public void ResolveTable_PathSatelliteWins_WhenBothPresent(string query, string expectedTable)
    {
        // The path-based mapping is the authoritative hint when present —
        // a user explicitly asking for `*/_Activity` paths gets the activities
        // table regardless of nodeType filter quirks.
        var parsed = _parser.Parse(query);
        var table = PostgreSqlPartitionedMeshQuery.ResolveTable(parsed);
        table.Should().Be(expectedTable);
    }

    // ─── Deep satellite paths — the "_Thread switches in, we stay there" rule ────
    //
    // Once a path contains a satellite segment (e.g. `_Thread`), every
    // descendant of that segment lives in the same satellite table — there
    // is no per-segment re-routing as the path deepens. This locks in the
    // prod 2026-05-23 sub-thread case
    //   Systemorph/_Thread/<thread>/<msg-id>/<sub-thread>/<sub-msg>
    // which must resolve to `threads` at every depth, not silently fall
    // back to `mesh_nodes` for the deeply-nested message ID. Missing one
    // of these cases is how "no messages showing here" started: the
    // satellite lookup walks the wrong table, returns null, and the chat
    // bubble subscribes to a stream that never emits content.

    [Theory]
    // Thread path itself
    [InlineData("path:Systemorph/_Thread/my-thread", "threads")]
    [InlineData("namespace:Systemorph/_Thread", "threads")]
    // ThreadMessage under a thread
    [InlineData("path:Systemorph/_Thread/my-thread/msg-id", "threads")]
    [InlineData("namespace:Systemorph/_Thread/my-thread", "threads")]
    // Delegated sub-thread under a message
    [InlineData("path:Systemorph/_Thread/my-thread/msg-id/sub-thread", "threads")]
    [InlineData("namespace:Systemorph/_Thread/my-thread/msg-id", "threads")]
    // Message under a delegated sub-thread (the prod 2026-05-23 path shape)
    [InlineData("path:Systemorph/_Thread/parent-thread/8721bdff/sub-thread/sub-msg-id", "threads")]
    [InlineData("namespace:Systemorph/_Thread/parent-thread/8721bdff/sub-thread", "threads")]
    // Even deeper (sub-sub-thread chain)
    [InlineData("path:Systemorph/_Thread/t1/m1/t2/m2/t3/m3", "threads")]
    // Other satellite types at depth
    [InlineData("path:Systemorph/_Activity/run-id/step-id/substep", "activities")]
    [InlineData("path:Systemorph/_Access/user_Access/scope-detail", "access")]
    [InlineData("path:Systemorph/_UserActivity/rbuergi/2026-05-23/entry-id", "user_activities")]
    [InlineData("path:Systemorph/_Comment/c1/replies/r1", "annotations")]
    public void ResolveTable_DeepSatellitePath_StaysInSatelliteTable(string query, string expectedTable)
    {
        var parsed = _parser.Parse(query);
        var table = PostgreSqlPartitionedMeshQuery.ResolveTable(parsed);
        table.Should().Be(expectedTable,
            "the satellite segment in the path must continue to route to its " +
            "table at every depth — there is no re-routing back to mesh_nodes " +
            "for deeply nested descendants. Breaking this rule produces the " +
            "'no messages showing' symptom: the satellite walk goes to the " +
            "wrong table, returns null, and consumers (chat bubbles, etc.) " +
            "subscribe to a stream that never emits.");
    }

    [Theory]
    // ResolveTable on PartitionDefinition operates on full paths (no `path:` prefix).
    // These exercise the boundary check in PathContainsSegment for deep paths.
    [InlineData("Systemorph/_Thread/parent", "threads")]
    [InlineData("Systemorph/_Thread/parent/msg-id", "threads")]
    [InlineData("Systemorph/_Thread/parent/msg-id/sub-thread", "threads")]
    [InlineData("Systemorph/_Thread/parent/msg-id/sub-thread/sub-msg-id", "threads")]
    [InlineData("Systemorph/_Activity/run/step/substep", "activities")]
    [InlineData("Systemorph/_UserActivity/rbuergi/2026-05-23/entry-id", "user_activities")]
    [InlineData("Systemorph/_Access/user_Access/role-detail", "access")]
    [InlineData("Systemorph/_Comment/c1/replies/r1", "annotations")]
    [InlineData("Systemorph/_Approval/a1/votes/v1", "annotations")]
    [InlineData("Systemorph/_Tracking/change-id/details/depth", "annotations")]
    [InlineData("Systemorph/_Notification/n1/follow-up", "notifications")]
    public void PartitionDefinition_ResolveTable_DeepPath_StaysInSatellite(string path, string expectedTable)
    {
        var def = new PartitionDefinition
        {
            Namespace = "Systemorph",
            Schema = "systemorph",
            Table = "mesh_nodes",
            TableMappings = PartitionDefinition.DefaultSegmentTableMappings(), NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings()
        };
        var table = def.ResolveTable(path);
        table.Should().Be(expectedTable);
    }

    [Fact]
    public void PartitionDefinition_ResolveTable_PlainPath_NoSatellite_FallsBackToMeshNodes()
    {
        var def = new PartitionDefinition
        {
            Namespace = "Systemorph",
            Schema = "systemorph",
            Table = "mesh_nodes",
            TableMappings = PartitionDefinition.DefaultSegmentTableMappings(), NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings()
        };

        def.ResolveTable("Systemorph").Should().Be("mesh_nodes");
        def.ResolveTable("Systemorph/Project").Should().Be("mesh_nodes");
        def.ResolveTable("Systemorph/Project/Doc/Page").Should().Be("mesh_nodes");
    }

    [Theory]
    // _ThreadMessage is longer than _Thread and must NOT be confused with
    // it. A path containing `_ThreadMessage` resolves to threads (same
    // table, kept for legacy paths); a path with only `_Thread` also
    // resolves to threads. The longest-suffix-wins ordering inside
    // ResolveTable prevents accidental misrouting between similar segments.
    [InlineData("Systemorph/_ThreadMessage/m1", "threads")]
    [InlineData("Systemorph/_ThreadMessage/m1/payload/depth", "threads")]
    public void PartitionDefinition_ResolveTable_ThreadMessageSegment_StaysInThreads(string path, string expectedTable)
    {
        var def = new PartitionDefinition
        {
            Namespace = "Systemorph",
            Schema = "systemorph",
            Table = "mesh_nodes",
            TableMappings = PartitionDefinition.DefaultSegmentTableMappings(), NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings()
        };
        def.ResolveTable(path).Should().Be(expectedTable);
    }

    // ─── ResolvePinnedPartition ────────────────────────────────────────

    [Theory]
    [InlineData("namespace:partition/*/_Thread", "partition")]
    [InlineData("namespace:partition/doc/_Thread", "partition")]
    [InlineData("namespace:Acme nodeType:Markdown", "acme")]
    public void ResolvePinnedPartition_FromConcreteFirstSegment(string query, string expected)
    {
        var parsed = _parser.Parse(query);
        var pinned = PostgreSqlPartitionedMeshQuery.ResolvePinnedPartition(parsed);
        pinned.Should().Be(expected, "concrete first segment must narrow fan-out to that one partition (lowercased to match the Postgres schema name)");
    }

    [Theory]
    [InlineData("namespace:*/_Thread")]
    [InlineData("nodeType:Thread")]
    [InlineData("source:activity scope:subtree is:main sort:LastModified-desc")]
    [InlineData("")]
    public void ResolvePinnedPartition_NullForWildcardOrUnscoped(string query)
    {
        var parsed = _parser.Parse(query);
        var pinned = PostgreSqlPartitionedMeshQuery.ResolvePinnedPartition(parsed);
        pinned.Should().BeNull("wildcard / unscoped queries fan out across every searchable partition");
    }

    [Fact]
    public void ResolvePinnedPartition_GlobalSatellite_PinsToRegisteredSchema()
    {
        // `_`-prefixed global namespaces have a schema name that differs from the namespace
        // (`_Access` → `system_access`). With the registered-partition resolver supplied,
        // the fan-out pins to that one real schema instead of discovering schemas via
        // information_schema.
        var parsed = _parser.Parse("namespace:_Access scope:children");
        var pinned = PostgreSqlPartitionedMeshQuery.ResolvePinnedPartition(
            parsed, seg => seg == "_Access" ? "system_access" : null);
        pinned.Should().Be("system_access");
    }

    [Fact]
    public void ResolvePinnedPartition_GlobalSatellite_NoResolverOrUnregistered_FansOut()
    {
        // No resolver, or a resolver that doesn't know the namespace → null → the discovery
        // fan-out. This is the prior behaviour, preserved as the correctness floor.
        var parsed = _parser.Parse("namespace:_Access scope:children");
        PostgreSqlPartitionedMeshQuery.ResolvePinnedPartition(parsed).Should().BeNull();
        PostgreSqlPartitionedMeshQuery.ResolvePinnedPartition(parsed, _ => null).Should().BeNull();
    }

    // ─── NeedsFanOut ───────────────────────────────────────────────────

    [Theory]
    [InlineData("nodeType:Thread namespace:*/_Thread content.createdBy:rbuergi sort:LastModified-desc")]
    [InlineData("source:activity scope:subtree is:main sort:LastModified-desc")]
    [InlineData("source:accessed scope:subtree is:main sort:LastModified-desc")]
    [InlineData("nodeType:Thread")]
    [InlineData("nodeType:Comment")]
    [InlineData("namespace:partition/*/_Thread")] // pinned partition + satellite → still through fan-out (per-schema satellite walk)
    public void NeedsFanOut_TrueForSatelliteOrUnscopedQueries(string query)
    {
        var parsed = _parser.Parse(query);
        PostgreSqlPartitionedMeshQuery.NeedsFanOut(parsed).Should().BeTrue();
    }

    [Theory]
    [InlineData("namespace:partition")]
    [InlineData("namespace:partition/doc")]
    [InlineData("namespace:partition nodeType:Markdown")]
    [InlineData("path:partition/doc")]
    public void NeedsFanOut_FalseForScopedPrimaryQueries(string query)
    {
        // Scoped queries against the primary mesh_nodes table — the
        // pedestrian StorageAdapterMeshQueryProvider handles these unchanged
        // via path routing.
        var parsed = _parser.Parse(query);
        PostgreSqlPartitionedMeshQuery.NeedsFanOut(parsed).Should().BeFalse();
    }
}
