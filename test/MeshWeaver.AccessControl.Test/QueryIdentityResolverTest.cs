using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.AccessControl.Test;

/// <summary>
/// The viewer-resolution rule, pinned as a pure function.
///
/// <para>It used to be copy-pasted into five providers — <c>StorageAdapterMeshQueryProvider</c>,
/// <c>PostgreSqlMeshQuery</c>, <c>PostgreSqlPartitionedMeshQuery</c>, <c>SnowflakeMeshQuery</c>,
/// <c>SnowflakePartitionedMeshQuery</c> — and the copies had DRIFTED in two places, so the same
/// request answered differently depending on which storage backend happened to serve it:</para>
/// <list type="bullet">
///   <item>an explicit <c>UserId = ""</c> meant "evaluate as the anonymous visitor" in the
///     pedestrian provider (its own xmldoc said so) and "ignore that and go look at the ambient
///     context" in all four native ones;</item>
///   <item>the System bypass was spelled <c>""</c> by two and <c>WellKnownUsers.System</c> by two,
///     and the fifth did not special-case it at all.</item>
/// </list>
/// <para>One resolver, one set of assertions.</para>
/// </summary>
public class QueryIdentityResolverTest
{
    private const string Ambient = "ambient-admin";

    [Fact]
    public void ExplicitUserId_Wins_OverAmbient()
    {
        var identity = QueryIdentityResolver.Resolve(MeshQueryRequest.FromQuery("path:X", "alice"), Ambient);

        identity.UserId.Should().Be("alice");
        identity.Source.Should().Be(QueryIdentitySource.Request);
        identity.IsUnresolved.Should().BeFalse();
    }

    /// <summary>
    /// 🚨 The divergence itself. The empty string is the "I mean the anonymous visitor" marker;
    /// falling through to the ambient context — as the four native providers did — hands a caller
    /// that deliberately asked for the anonymous view the AMBIENT user's view instead, which on a
    /// portal is whoever the surrounding request belongs to.
    /// </summary>
    [Fact]
    public void ExplicitEmptyUserId_MeansAnonymous_AndNeverFallsThroughToAmbient()
    {
        var identity = QueryIdentityResolver.Resolve(MeshQueryRequest.FromQuery("path:X", ""), Ambient);

        identity.UserId.Should().Be(WellKnownUsers.Anonymous);
        identity.Source.Should().Be(QueryIdentitySource.Request);
        identity.IsAnonymous.Should().BeTrue();
    }

    [Fact]
    public void SystemIdentity_BypassesRowLevelSecurity_AndHasOneSpelling()
    {
        var identity = QueryIdentityResolver.Resolve(MeshQueryRequest.FromQuery("path:X").AsSystem(), Ambient);

        identity.IsSystem.Should().BeTrue();
        identity.UserId.Should().Be(WellKnownUsers.System, "the partitioned providers key on the literal");
        identity.RlsUserId.Should().BeEmpty("…and the non-partitioned ones pass \"\" to mean 'no user filter'");
    }

    [Fact]
    public void NoUserId_ResolvesFromTheAmbientContext()
    {
        var identity = QueryIdentityResolver.Resolve(MeshQueryRequest.FromQuery("path:X"), Ambient);

        identity.UserId.Should().Be(Ambient);
        identity.Source.Should().Be(QueryIdentitySource.Ambient);
        identity.IsUnresolved.Should().BeFalse();
    }

    [Fact]
    public void NothingNamesAViewer_ResolvesAnonymous_ButFlagsItUnresolved()
    {
        var identity = QueryIdentityResolver.Resolve(MeshQueryRequest.FromQuery("path:X"), ambientUserId: null);

        identity.UserId.Should().Be(WellKnownUsers.Anonymous, "the fallback must never WIDEN the read");
        identity.IsUnresolved.Should().BeTrue("…but it must be distinguishable from a deliberate Anonymous read");
    }

    [Fact]
    public void DeclaredPublicListing_IsAnonymousByIntent_NotUnresolved()
    {
        var identity = QueryIdentityResolver.Resolve(
            MeshQueryRequest.FromQuery("nodeType:Course").AsPublicListing(), ambientUserId: null);

        identity.IsAnonymous.Should().BeTrue();
        identity.Source.Should().Be(QueryIdentitySource.PublicListing);
        identity.IsUnresolved.Should().BeFalse("a declared listing has nothing to diagnose");
    }

    /// <summary>
    /// A declared public listing must not pick the caller up even when one IS ambient — that is the
    /// #415 duplicate-cards failure: a catalog that stamps the visitor folds their own private
    /// copies into the public list.
    /// </summary>
    [Fact]
    public void DeclaredPublicListing_IgnoresTheAmbientViewer()
    {
        var identity = QueryIdentityResolver.Resolve(
            MeshQueryRequest.FromQuery("nodeType:Course").AsPublicListing(), Ambient);

        identity.UserId.Should().Be(WellKnownUsers.Anonymous);
    }

    [Fact]
    public void RequireViewer_FailsClosed_WhenNothingNamesAViewer()
    {
        var request = MeshQueryRequest.FromQuery("path:Alice/Secret").RequireViewer();
        var identity = QueryIdentityResolver.Resolve(request, ambientUserId: null);

        Action act = () => identity.EnsureResolved(request);

        act.Should().Throw<QueryIdentityUnresolvedException>()
            .Which.Message.Should().Contain("path:Alice/Secret");
    }

    [Fact]
    public void RequireViewer_IsANoOp_WhenAViewerResolves()
    {
        var request = MeshQueryRequest.FromQuery("path:Alice/Secret").RequireViewer();

        QueryIdentityResolver.Resolve(request, Ambient).EnsureResolved(request)
            .UserId.Should().Be(Ambient);
    }

    /// <summary>
    /// The diagnostic's blast-radius control: only a read aimed INTO a named partition is worth
    /// warning about. An unscoped read that evaluates as Anonymous returns the mesh's public
    /// subset — a sensible answer, and the shape a genuine catalog has.
    /// </summary>
    [Theory]
    [InlineData("path:Alice/Secret", true)]
    [InlineData("namespace:ACME nodeType:Story", true)]
    [InlineData("nodeType:Course", false)]
    [InlineData("", false)]
    [InlineData("path:* nodeType:Story", false)]
    [InlineData("namespace:*/_Thread", false)]
    public void TargetsNamedPartition_DiscriminatesScopedReadsFromMeshWideOnes(string query, bool expected)
        => QueryIdentityResolver.TargetsNamedPartition(MeshQueryRequest.FromQuery(query))
            .Should().Be(expected);
}
