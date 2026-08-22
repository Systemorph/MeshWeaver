using System.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// 🚨 A PERMISSION MUST NEVER BE DECIDED ON A PAGE — issue #2011.
///
/// <para>The security fold's global reads (<c>$security-roles</c>, <c>$security-memberships</c>,
/// the per-gated-type queries) carry no <c>path:</c> and no <c>namespace:</c>, because the subject
/// and the grant that names it may live in different partitions. On Postgres that is the shape
/// <c>PostgreSqlCrossSchemaQueryProvider</c> answers with a 50-row PAGE when the caller states no
/// limit — and here the caller CANNOT state one, because
/// <c>IMeshNodeStreamCache.GetQuery</c> takes query STRINGS and builds its own
/// <see cref="MeshQueryRequest"/>, so <see cref="MeshQueryRequest.Complete"/> is out of reach.</para>
///
/// <para>These assertions are the cheap half of the fix and they name what the behavioural test
/// (<c>SecurityFoldEnumerationTests</c>, on Postgres) protects: every security query declares itself
/// an ENUMERATION, and it does so through a builder that cannot produce a truncatable string.</para>
/// </summary>
public class SecurityQueryCompletenessTests
{
    private static readonly QueryParser Parser = new();

    /// <summary>The query-string spelling of <c>Complete()</c>, since the fold can only pass strings.</summary>
    [Fact]
    public void LimitAll_ParsesToNoLimit()
    {
        Parser.Parse($"nodeType:Role scope:subtree {MeshQueryRequest.CompleteQualifier}").Limit
            .Should().Be(MeshQueryRequest.NoLimit,
                "limit:all is the query-string form of MeshQueryRequest.Complete() — without the "
                + "parser mapping it, the qualifier would be silently ignored and the query would "
                + "still be served as a page");
        MeshQueryRequest.CompleteQualifier.Should().Be("limit:all");
    }

    /// <summary>
    /// The CONTROL for the behavioural test: this is the string the fold used to issue, and it is
    /// exactly the shape that gets a page. If this ever starts parsing to a limit on its own, the
    /// regression test below has stopped proving anything.
    /// </summary>
    [Fact]
    public void TheOldUnpinnedShape_StatesNoLimitAtAll()
        => Parser.Parse("nodeType:GroupMembership scope:subtree select:path,id,namespace,name,nodeType,content")
            .Limit.Should().BeNull(
                "an unpinned query that states no limit is served the cross-schema fan-out's "
                + "default page, and a truncated membership list is indistinguishable from 'this "
                + "viewer is in no groups'");

    [Fact]
    public void EverySecurityQueryShape_IsAnEnumeration()
    {
        foreach (var shape in SecurityQueries.AllShapes)
            Parser.Parse(shape).Limit.Should().Be(MeshQueryRequest.NoLimit,
                $"'{shape}' decides a permission — a missing row here does not shorten a list, it "
                + "removes an access right (or, for a group-scoped deny, fails to remove one)");
    }

    [Fact]
    public void GlobalQueries_KeepTheirShape()
    {
        SecurityQueries.Roles.Should().Be(
            "nodeType:Role scope:subtree select:path,id,namespace,name,nodeType,content limit:all");
        SecurityQueries.Memberships.Should().Be(
            "nodeType:GroupMembership scope:subtree select:path,id,namespace,name,nodeType,content limit:all");
        SecurityQueries.GatedNodes("Store/Plugin").Should().Be(
            "nodeType:Store/Plugin scope:subtree select:path,id,namespace,name,nodeType limit:all");
    }

    /// <summary>
    /// <see cref="SecurityQueries.Enumeration"/> is the seam the whole fold funnels through, so it
    /// has to be total: idempotent, and it must OVERWRITE a stated limit rather than honour it —
    /// in this fold a page IS the bug, whoever asked for it.
    /// </summary>
    [Theory]
    [InlineData("nodeType:Role scope:subtree", "nodeType:Role scope:subtree limit:all")]
    [InlineData("nodeType:Role scope:subtree limit:all", "nodeType:Role scope:subtree limit:all")]
    [InlineData("nodeType:Role scope:subtree limit:10", "nodeType:Role scope:subtree limit:all")]
    [InlineData("limit:5 nodeType:Role", "limit:all nodeType:Role")]
    [InlineData("  nodeType:Role  ", "nodeType:Role limit:all")]
    public void Enumeration_StampsCompleteness(string input, string expected)
        => SecurityQueries.Enumeration(input).Should().Be(expected);

    /// <summary>A filter that merely CONTAINS "limit:" is not the qualifier and must survive.</summary>
    [Fact]
    public void Enumeration_LeavesNonQualifierTokensAlone()
        => SecurityQueries.Enumeration("nodeType:Role content.limit:3")
            .Should().Be("nodeType:Role content.limit:3 limit:all");

    /// <summary>
    /// Anchored scope walks go through the same stamp. The ROOT scope's anchor (<c>_Access</c>, and
    /// the empty namespace for <c>_Policy</c>) resolves to NO partition, so it falls through to the
    /// very same cross-schema fan-out as the global reads — which is why "it is anchored" is not a
    /// reason to leave one unstamped.
    /// </summary>
    [Fact]
    public void ScopedQueries_AreStampedToo()
    {
        var rootAccess = SecurityQueries.Scoped(
            $"namespace:_Access nodeType:AccessAssignment {SecurityQueries.ContentProjection}");
        Parser.Parse(rootAccess).Limit.Should().Be(MeshQueryRequest.NoLimit);
        SecurityQueries.AllShapes.Should().Contain(rootAccess);
    }

    [Fact]
    public void Enumeration_RejectsAnEmptyQuery()
    {
        Assert.Throws<System.ArgumentException>(() => SecurityQueries.Enumeration("   "));
    }
}
