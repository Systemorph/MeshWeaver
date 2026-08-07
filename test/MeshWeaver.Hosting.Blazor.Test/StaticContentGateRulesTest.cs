using MeshWeaver.Hosting.Blazor;
using Xunit;

namespace MeshWeaver.Hosting.Blazor.Test;

/// <summary>
/// The PURE rules behind the <c>/static</c> gate (issue #587): which mesh node a served file is
/// attributed to, which paths are refused outright, and how the response may be cached. Pinned
/// without a file system or a hub, so a regression shows up as a failing rule rather than as a
/// leak that only a full integration run would notice.
/// </summary>
public class StaticContentGateRulesTest
{
    // ── Attribution: the raw mesh-level backing store ───────────────────────────────────────

    /// <summary>
    /// The store's layout is <c>{mount}/{nodePath}/…</c> because the per-node mounts are what
    /// create it (<c>content/{nodePath}</c>, <c>attachments/{nodePath}</c>). Dropping the mount
    /// segment is the exact inverse of the URL every producer builds.
    /// </summary>
    [Theory]
    [InlineData("content/ACME/logo.svg", "ACME/logo.svg")]
    [InlineData("content/GatedCourse/PaidLesson/video.mp4", "GatedCourse/PaidLesson/video.mp4")]
    [InlineData("attachments/ACME/Project/datacube.csv", "ACME/Project/datacube.csv")]
    public void OwnerCandidate_DropsTheMountSegment(string filePath, string expected) =>
        StaticContentGate.RootStoreOwnerCandidate(filePath).Should().Be(expected);

    /// <summary>
    /// A file directly at the store root belongs to no node. There is nothing to check a
    /// permission against, so the gate must have nothing to work with — and deny.
    /// </summary>
    [Theory]
    [InlineData("loose.txt")]
    [InlineData("")]
    [InlineData(null)]
    public void OwnerCandidate_IsNullWhenTheFileCannotBeAttributed(string? filePath) =>
        StaticContentGate.RootStoreOwnerCandidate(filePath).Should().BeNull();

    // ── Attribution: the address-based pattern ──────────────────────────────────────────────

    /// <summary>
    /// The collection is mounted on the resolved node, so that node is the owner FLOOR; appending
    /// the file path lets the resolver attribute the file to a deeper node when the content
    /// mirrors the node tree — which is what keeps a paid lesson's media gated.
    /// </summary>
    [Fact]
    public void AddressOwnerCandidate_AppendsTheFilePathToTheMountedNode() =>
        StaticContentGate.AddressOwnerCandidate("GatedCourse", "PaidLesson/video.mp4")
            .Should().Be("GatedCourse/PaidLesson/video.mp4");

    // ── The traversal guard ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 THE ATTRIBUTION BYPASS. <c>FileSystemStreamProvider</c> does a bare
    /// <c>Path.Combine(basePath, path)</c>, and the catch-all route hands this endpoint
    /// percent-encoded segments that <c>DecodeContentPath</c> un-escapes — so a
    /// <c>%2E%2E</c> segment reaches the gate as <c>..</c> AFTER the server's own URL
    /// normalization. Without this rule the candidate <c>PublicSpace/../PrivateSpace/secret.pdf</c>
    /// resolves to <c>PublicSpace</c>, borrows its anonymous grant, and then reads the OTHER
    /// partition's file. Dot and empty segments are refused, so the candidate is null and the
    /// request is denied.
    /// </summary>
    [Theory]
    [InlineData("content/PublicSpace/../PrivateSpace/secret.pdf")]
    [InlineData("content/PublicSpace/./secret.pdf")]
    [InlineData("content/PublicSpace//secret.pdf")]
    public void OwnerCandidate_RefusesTraversalAndEmptySegments(string filePath) =>
        StaticContentGate.RootStoreOwnerCandidate(filePath).Should().BeNull(
            "a traversal segment would let a file borrow another node's grant");

    /// <summary>
    /// The same guard on the address shape — the mounted node must not be able to lend its grant
    /// to a path that walks out of its own subtree.
    /// </summary>
    [Fact]
    public void AddressOwnerCandidate_RefusesTraversal() =>
        StaticContentGate.AddressOwnerCandidate("PublicSpace", "../PrivateSpace/secret.pdf")
            .Should().BeNull();

    /// <summary>
    /// A double quote would break out of the quoting <c>IPathResolver</c> puts around each segment
    /// when it builds its <c>path:</c> query — the attribution query is not a place to accept
    /// caller-controlled quoting.
    /// </summary>
    [Fact]
    public void OwnerCandidate_RefusesAQuoteThatWouldBreakTheResolverQuery() =>
        StaticContentGate.RootStoreOwnerCandidate("content/AC\"ME/logo.svg").Should().BeNull();

    /// <summary>Ordinary names — dots inside a segment, spaces, unicode — must still resolve.</summary>
    [Theory]
    [InlineData("content/ACME/module1-intro.poster.png", "ACME/module1-intro.poster.png")]
    [InlineData("content/Data Analytics/report.pdf", "Data Analytics/report.pdf")]
    [InlineData("content/Übersicht/plan.pdf", "Übersicht/plan.pdf")]
    public void OwnerCandidate_AcceptsOrdinaryFileNames(string filePath, string expected) =>
        StaticContentGate.RootStoreOwnerCandidate(filePath).Should().Be(expected);

    // ── Caching ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Access-controlled bytes must never be stored by a CDN / corporate proxy — an intermediary
    /// that saw one authorized fetch would keep replaying it to callers the gate denies, so the
    /// leak would survive the fix. Only the declared-public asset class is shared-cacheable.
    /// </summary>
    [Fact]
    public void CacheControl_IsPrivateForGatedContent()
    {
        var gated = BlazorHostingExtensions.CacheControlFor(isPublic: false);
        gated.Should().Contain("private");
        gated.Should().NotContain("public");
        gated.Should().NotContain("immutable",
            "an immutable 30-day promise outlives any revocation of the grant");
    }

    /// <summary>The public asset class keeps its long, shared-cacheable, immutable response.</summary>
    [Fact]
    public void CacheControl_StaysPublicAndImmutableForDeclaredPublicCollections()
    {
        var declaredPublic = BlazorHostingExtensions.CacheControlFor(isPublic: true);
        declaredPublic.Should().Contain("public");
        declaredPublic.Should().Contain("immutable");
    }
}
