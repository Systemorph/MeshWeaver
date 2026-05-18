using FluentAssertions;
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
    [InlineData("nodeType:Organization")]
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
