using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// #15 root-cause guard. <see cref="PostgreSqlPathRoutingAdapter.IsValidPartitionSegment"/>
/// must reject URL/query-string-shaped path segments so the partition router NEVER
/// lazily <c>CREATE SCHEMA</c>s a garbage schema. Prod 2026-06-05: the atioz DB filled
/// with schemas like <c>login?error=auth_failed</c> and
/// <c>search?q=agent&amp;hq=scope%3adescendants</c> — request URLs routed as mesh paths —
/// and corrupted itself. Pure validation: no DB, no Docker.
/// </summary>
public class PartitionSegmentValidationTest
{
    [Theory]
    [InlineData("login?error=auth_failed")]                  // the real atioz garbage schemas
    [InlineData("search?q=agent&hq=scope%3adescendants")]
    [InlineData("search?q=coder&hq=scope%3adescendants")]
    [InlineData("a b")]                                       // whitespace
    [InlineData("ns:with:colons")]                            // colons
    [InlineData("path/with/slash")]                           // slash
    [InlineData("name#frag")]                                 // fragment
    [InlineData("")]                                          // empty
    [InlineData("_access")]                                   // leading underscore (not a partition name)
    public void Rejects_NonIdentifierSegments(string seg)
        => PostgreSqlPathRoutingAdapter.IsValidPartitionSegment(seg).Should().BeFalse(
            "URL/query-string/garbage segments must never become a Postgres schema");

    [Theory]
    [InlineData("rbuergi")]
    [InlineData("rsalzmann")]
    [InlineData("Systemorph")]
    [InlineData("acme")]
    [InlineData("roland.buergi")]
    [InlineData("space-1")]
    [InlineData("my_space")]
    [InlineData("a")]
    public void Accepts_ValidPartitionNames(string seg)
        => PostgreSqlPathRoutingAdapter.IsValidPartitionSegment(seg).Should().BeTrue(
            "a simple identifier is a valid partition / schema name");

    [Fact]
    public void Rejects_TooLong()
        => PostgreSqlPathRoutingAdapter.IsValidPartitionSegment(new string('a', 64)).Should().BeFalse(
            "Postgres identifiers are capped at 63 characters");
}
