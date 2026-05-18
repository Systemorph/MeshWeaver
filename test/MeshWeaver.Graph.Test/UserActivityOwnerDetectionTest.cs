using FluentAssertions;
using MeshWeaver.Graph;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Unit tests for <see cref="UserActivityLayoutAreas.IsViewerOwner"/> — the
/// gate that decides between rendering the owner dashboard (Latest Threads,
/// Activity Feed, Recently Viewed) and the visitor profile when a user
/// navigates to <c>/{username}</c>.
///
/// <para>Prod symptom that drove this: navigating to <c>/rbuergi</c> as
/// rbuergi renders the visitor profile (no Latest Threads) because the
/// original gate compared <c>AccessContext.ObjectId</c> directly to the
/// partition key with no fallback. Different auth backends populate
/// ObjectId differently (Entra GUID, UPN, alias), and the
/// <c>LayoutAreaHost</c>'s per-subscription Context only flows during
/// initialization, not into the downstream observable.</para>
/// </summary>
public class UserActivityOwnerDetectionTest
{
    [Theory]
    [InlineData("rbuergi", "rbuergi", null, true)]
    [InlineData("RBUERGI", "rbuergi", null, true)] // case-insensitive
    [InlineData("rbuergi", "Rbuergi", null, true)]
    public void IsViewerOwner_TrueOnObjectIdMatch(string objectId, string partitionKey, string? email, bool expected)
    {
        var ctx = new AccessContext { ObjectId = objectId, Email = email ?? "" };
        UserActivityLayoutAreas.IsViewerOwner(ctx, partitionKey).Should().Be(expected);
    }

    [Theory]
    // ObjectId is the UPN — the dashboard hub's partition key is the email's local part.
    // Auth backend variant: Entra surfaces ObjectId as the UPN. The fallback below kicks in.
    [InlineData("rbuergi@systemorph.com", "rbuergi", "rbuergi@systemorph.com", true)]
    // ObjectId is a GUID (Entra OID) — no direct match; email alias resolves the partition.
    [InlineData("12345678-1234-1234-1234-123456789abc", "rbuergi", "rbuergi@systemorph.com", true)]
    // Case-insensitive on the email-local-part too.
    [InlineData("anything", "rbuergi", "RBUERGI@SYSTEMORPH.COM", true)]
    public void IsViewerOwner_TrueOnEmailAliasMatch(string objectId, string partitionKey, string email, bool expected)
    {
        var ctx = new AccessContext { ObjectId = objectId, Email = email };
        UserActivityLayoutAreas.IsViewerOwner(ctx, partitionKey).Should().Be(expected);
    }

    [Theory]
    [InlineData("alice", "bob", "alice@example.com", false)]   // viewing someone else's dashboard
    [InlineData("", "rbuergi", null, false)]                    // empty ObjectId, no email
    [InlineData("rbuergi-bot", "rbuergi", null, false)]         // similar but different key
    [InlineData("rbuergi@other.com", "rbuergi", "rbuergi@other.com", true)] // alias still wins even with different domain
    public void IsViewerOwner_FalseOnMismatch(string objectId, string partitionKey, string? email, bool expected)
    {
        var ctx = new AccessContext { ObjectId = objectId, Email = email ?? "" };
        UserActivityLayoutAreas.IsViewerOwner(ctx, partitionKey).Should().Be(expected);
    }

    [Fact]
    public void IsViewerOwner_NullContext_FalsePerInvariant()
    {
        UserActivityLayoutAreas.IsViewerOwner(null, "rbuergi").Should().BeFalse(
            "an unauthenticated viewer never gets the owner dashboard");
    }

    [Fact]
    public void IsViewerOwner_EmptyPartitionKey_False()
    {
        var ctx = new AccessContext { ObjectId = "rbuergi", Email = "rbuergi@x.com" };
        UserActivityLayoutAreas.IsViewerOwner(ctx, "").Should().BeFalse(
            "an empty partition key has no valid owner to match against");
    }
}
