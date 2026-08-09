using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// #15 / #714 root-cause guard. <see cref="PartitionDefinition.IsValidPartitionSegment"/>
/// — the ONE shared charset rule used by the storage routers
/// (<c>PostgreSqlPathRoutingAdapter</c> / <c>SnowflakePathRoutingAdapter</c>) AND the
/// provisioning boundary (<c>EnsurePartitionProvisioned</c> /
/// <c>OwnsPartitionProvisioningValidator</c>) — must reject URL/query-string-shaped path
/// segments so a garbage schema can neither be routed to NOR provisioned. Prod 2026-06-05:
/// the prod DB filled with schemas like <c>login?error=auth_failed</c> and
/// <c>search?q=agent&amp;hq=scope%3adescendants</c> — request URLs routed as mesh paths —
/// and corrupted itself; #714 found the same junk (query-string schemas, each with its own
/// <c>mesh_nodes</c>) in the memex and the cloud database databases. Pure validation: no DB, no Docker.
/// </summary>
public class PartitionSegmentValidationTest
{
    [Theory]
    [InlineData("login?error=auth_failed")]                  // the real prod garbage schemas
    [InlineData("search?q=agent&hq=scope%3adescendants")]
    [InlineData("search?q=coder&hq=scope%3adescendants")]
    // The literal junk schema names found in the memex/the cloud database DBs (#714):
    [InlineData("search?q=query%20syntax&hq=scope%3adescendants")]
    [InlineData("login?returnurl=https%3a%2f%2fportal.example.com%2fauthorize")]
    [InlineData("a b")]                                       // whitespace
    [InlineData("ns:with:colons")]                            // colons
    [InlineData("path/with/slash")]                           // slash
    [InlineData("name#frag")]                                 // fragment
    [InlineData("name=value")]                                // '='
    [InlineData("a&b")]                                       // '&'
    [InlineData("100%hundred")]                               // '%'
    [InlineData("")]                                          // empty
    [InlineData("_access")]                                   // leading underscore (not a partition name)
    public void Rejects_NonIdentifierSegments(string seg)
        => PartitionDefinition.IsValidPartitionSegment(seg).Should().BeFalse(
            "URL/query-string/garbage segments must never become a schema — routed OR provisioned");

    [Fact]
    public void Rejects_Null()
        => PartitionDefinition.IsValidPartitionSegment(null).Should().BeFalse(
            "a null id can never name a partition");

    [Theory]
    [InlineData("rbuergi")]
    [InlineData("rsalzmann")]
    [InlineData("Systemorph")]
    [InlineData("acme")]
    [InlineData("roland.buergi")]
    [InlineData("space-1")]
    [InlineData("my_space")]
    [InlineData("a")]
    // The rule is Unicode-aware (char.IsLetterOrDigit), NOT an [A-Za-z0-9._-] whitelist — an
    // accented partition name is legitimate and must not be refused (nor dropped by V51).
    [InlineData("müller")]
    [InlineData("Straße")]
    public void Accepts_ValidPartitionNames(string seg)
        => PartitionDefinition.IsValidPartitionSegment(seg).Should().BeTrue(
            "a simple identifier is a valid partition / schema name");

    [Fact]
    public void Accepts_ExactlyAtTheByteLimit()
        => PartitionDefinition.IsValidPartitionSegment(new string('a', 63)).Should().BeTrue(
            "63 bytes is Postgres' NAMEDATALEN limit — the boundary itself is still valid");

    [Theory]
    [InlineData(64, 'a')]   // 64 ASCII chars = 64 bytes
    [InlineData(32, 'ü')]   // 32 two-byte chars = 64 bytes, but only 32 CHARS
    public void Rejects_LongerThan63Utf8Bytes(int count, char fill)
        => PartitionDefinition.IsValidPartitionSegment(new string(fill, count)).Should().BeFalse(
            "Postgres identifiers are capped at 63 BYTES (NAMEDATALEN) and silently truncate "
            + "beyond it — a char-counted cap would let a multi-byte name be provisioned under a "
            + "truncated schema the router could never route back to");
}
