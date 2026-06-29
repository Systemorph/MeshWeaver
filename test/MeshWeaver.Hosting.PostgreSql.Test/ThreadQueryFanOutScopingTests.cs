using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Pins the "thread list must stay on its own partition" contract (the fix for the
/// fan-out that wedges the portal). A <c>nodeType:Thread</c> query resolves to the
/// <c>threads</c> SATELLITE table; when it carries no concrete partition the cross-schema
/// provider UNIONs that satellite across EVERY searchable schema — an unbounded,
/// all-partition scan. Issuing that unscoped form (e.g. <c>nodeType:Thread</c> with no
/// namespace) is what took the portal down.
///
/// <para>The fix: default thread lists scope to the viewer's OWN partition (derived from
/// the layout area's hub, not the URL) as <c>namespace:{partition}/*_Thread</c>. That form
/// has a concrete first segment, so <see cref="PostgreSqlPartitionedMeshQuery.ResolvePinnedPartition"/>
/// PINS it to the single partition schema — the fan-out narrows to one schema and never
/// scans every partition. These are pure-logic assertions on the routing decision (no PG
/// fixture needed).</para>
/// </summary>
public class ThreadQueryFanOutScopingTests
{
    private readonly QueryParser _parser = new();

    [Fact]
    public void PartitionScopedThreadList_PinsToSinglePartition_NoFanOut()
    {
        // The shape UserActivityLayoutAreas.BuildLatestThreads now emits.
        var parsed = _parser.Parse("nodeType:Thread namespace:rbuergi/*_Thread -content.status:Done sort:LastModified-desc");

        PostgreSqlPartitionedMeshQuery.ResolvePinnedPartition(parsed)
            .Should().Be("rbuergi",
                "a thread list scoped to the partition must pin to that one schema — never UNION across every partition");
    }

    [Theory]
    [InlineData("nodeType:Thread")]                                   // fully unscoped — THE query that wedged
    [InlineData("nodeType:Thread sort:LastModified-desc")]
    [InlineData("nodeType:Thread namespace:*/_Thread")]              // explicit cross-partition wildcard
    [InlineData("nodeType:Thread namespace:*/_Thread content.createdBy:rbuergi")] // the OLD user-home query
    public void UnscopedThreadList_DoesNotPin_FansOutAcrossAllSchemas(string query)
    {
        var parsed = _parser.Parse(query);

        PostgreSqlPartitionedMeshQuery.ResolvePinnedPartition(parsed)
            .Should().BeNull(
                "an unscoped / wildcard-first thread query cannot be pinned, so it fans out the threads " +
                "satellite across every searchable schema — the unbounded all-partition scan this fix avoids");
    }

    [Fact]
    public void SpaceScopedThreadList_PinsToSpacePartition()
    {
        // The existing SpaceLayoutAreas form — confirms the partition-scoped pattern pins generally.
        var parsed = _parser.Parse("nodeType:Thread namespace:Acme/*/_Thread sort:LastModified-desc");

        PostgreSqlPartitionedMeshQuery.ResolvePinnedPartition(parsed)
            .Should().Be("acme",
                "namespace:{space}/*/_Thread has a concrete first segment and must pin to that space's schema");
    }
}
