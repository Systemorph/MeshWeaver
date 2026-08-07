using MeshWeaver.ContentCollections;
using MeshWeaver.Graph;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Blazor.Test;

/// <summary>
/// 🚨 THE REGRESSION GUARD for issue #587, at the point the bug is actually introduced: URL
/// GENERATION.
///
/// <para><c>/static</c> resolves no identity and evaluates no permission — everything reachable
/// there is public to the internet. So the security property is not "the endpoint checks"; it is
/// that <b>nothing generates a <c>/static</c> URL for mesh content</b>. Every helper below used to,
/// and between them they cover essentially every image, icon, thumbnail, PDF, download link and
/// markdown asset the product renders.</para>
///
/// <para>These are pure functions, so the contract is pinned without a mesh, a browser or a request.
/// A change that re-points any of them at <c>/static</c> fails here immediately — which is the whole
/// point: the original hole was invisible precisely because a <c>/static</c> URL works perfectly,
/// for everyone.</para>
/// </summary>
public class ContentUrlGenerationTest
{
    private const string PrivateNode = "PrivateSpace/Project";

    /// <summary>
    /// The canonical builder. Everything that serves a collection file must route through it, so a
    /// single place decides the shape.
    /// </summary>
    [Fact]
    public void GetContentFileUrl_UsesTheAccessControlledRoute() =>
        ContentCollectionsExtensions.GetContentFileUrl(PrivateNode, "content", "logo.svg")
            .Should().Be("/api/content/PrivateSpace/Project/content/logo.svg");

    /// <summary>
    /// The node-relative shape that replaces <c>/static/storage/content/{node}/{file}</c> — the
    /// mesh-level backing store URL that exposed every partition's uploads.
    /// </summary>
    [Fact]
    public void GetNodeContentFileUrl_ReplacesTheBackingStoreShape() =>
        ContentCollectionsExtensions.GetNodeContentFileUrl(PrivateNode, "logo.svg")
            .Should().Be("/api/content/PrivateSpace/Project/logo.svg");

    /// <summary>
    /// The central icon resolver. <c>content:</c> on a node is the single most common way a private
    /// Space's image gets a URL, and it pointed straight at the unauthenticated store.
    /// </summary>
    [Theory]
    [InlineData("content:logo.svg")]
    [InlineData("content/logo.svg")]
    public void MeshNodeImageHelper_ResolvesContentReferences_ToTheContentRoute(string icon) =>
        MeshNodeImageHelper.ResolveContentPath(icon, PrivateNode)
            .Should().Be("/api/content/PrivateSpace/Project/logo.svg");

    /// <summary>
    /// A node's resolved icon must never land on <c>/static</c> unless it is a NODE-TYPE ICON — the
    /// one asset class that is genuinely build output (an SVG compiled into MeshWeaver.Graph, no
    /// user data, needed before any identity exists).
    /// </summary>
    [Fact]
    public void ResolveNodeIcon_OnlyEverUsesStatic_ForBuildAssetIcons()
    {
        var withContentIcon = MeshNodeImageHelper.ResolveNodeIcon(
            new MeshNode("Project", "PrivateSpace") { Icon = "content:logo.svg", NodeType = "Markdown" });
        withContentIcon.Should().Be("/api/content/PrivateSpace/Project/logo.svg");

        var withoutIcon = MeshNodeImageHelper.ResolveNodeIcon(
            new MeshNode("Project", "PrivateSpace") { NodeType = "Markdown" });
        withoutIcon.Should().StartWith("/static/NodeTypeIcons/",
            "a NodeType's default glyph is a build asset — that is the only /static an icon may use");
    }

    /// <summary>
    /// EVERY image in EVERY markdown document goes through this rewrite. It emitted
    /// <c>static/{collection}/{path}</c>, so an image inside a private Space was readable by anyone
    /// with the URL.
    /// </summary>
    [Fact]
    public void MarkdownImageRewrite_UsesTheContentRoute()
    {
        var href = MarkdownExtensions.ToContentHref("diagram.svg", PrivateNode);

        href.Should().Be("api/content/PrivateSpace/Project/diagram.svg");
        href.Should().NotContain("static",
            "a markdown image in a private Space must not be served by the unauthenticated route");
    }

    /// <summary>A resource referenced from collection content resolves against its owning node.</summary>
    [Fact]
    public void AdaptResourceUrl_UsesTheContentRoute() =>
        ContentCollectionsExtensions.AdaptResourceUrl("diagram.svg", "content", (Address)PrivateNode)
            .Should().Be("/api/content/PrivateSpace/Project/content/diagram.svg");

    /// <summary>Absolute and already-rooted URLs are left exactly as the author wrote them.</summary>
    [Theory]
    [InlineData("https://example.com/logo.png")]
    [InlineData("/api/content/Other/logo.png")]
    public void AdaptResourceUrl_LeavesAbsoluteUrlsAlone(string url) =>
        ContentCollectionsExtensions.AdaptResourceUrl(url, "content", (Address)PrivateNode)
            .Should().Be(url);

    /// <summary>
    /// An address-less collection cannot be attributed to an owning node, so there is no permission
    /// to evaluate and no safe URL to build. It must NOT fall back to <c>/static</c> — that was
    /// precisely the unauthenticated path.
    /// </summary>
    [Fact]
    public void AdaptResourceUrl_WithNoOwningAddress_DoesNotFallBackToStatic() =>
        ContentCollectionsExtensions.AdaptResourceUrl("diagram.svg", "content", null)
            .Should().Be("diagram.svg");

    /// <summary>
    /// Stored branding paths must still resolve — including the pre-#587 URLs persisted on nodes
    /// written before the fix. Dropping the legacy shape would silently blank those logos.
    /// </summary>
    [Theory]
    [InlineData("content:Brand/logo.svg", "Brand/logo.svg")]
    [InlineData("/static/storage/content/Brand/logo.svg", "Brand/logo.svg")]
    [InlineData("/api/content/Brand/content/logo.svg", "Brand/logo.svg")]
    public void ContentPathReference_ReadsEveryStoredShape(string stored, string expected) =>
        ContentPathReference.TryGetRelativePath(stored).Should().Be(expected);

    /// <summary>An absolute URL is not a content reference and must not be mistaken for one.</summary>
    [Fact]
    public void ContentPathReference_RejectsAbsoluteUrls() =>
        ContentPathReference.TryGetRelativePath("https://example.com/logo.svg").Should().BeNull();

    /// <summary>
    /// The traversal guard, pinned as a pure rule. <c>FileSystemStreamProvider</c> resolves a
    /// collection-relative path with a bare <c>Path.Combine</c>, so a surviving <c>..</c> reads
    /// outside the collection's BasePath — another partition's files, under a grant the caller
    /// legitimately holds for this one.
    /// </summary>
    [Theory]
    [InlineData("..")]
    [InlineData("a/../b")]
    [InlineData("a/./b")]
    [InlineData("a//b")]
    [InlineData("a\\b")]
    [InlineData("")]
    public void UnsafePaths_AreRejected(string path) =>
        StaticAssetMount.IsSafeRelativePath(path).Should().BeFalse();

    /// <summary>…and an ordinary nested path is still fine.</summary>
    [Theory]
    [InlineData("box.svg")]
    [InlineData("videos/intro.mp4")]
    [InlineData("a.b/c.d")]
    public void OrdinaryPaths_AreAccepted(string path) =>
        StaticAssetMount.IsSafeRelativePath(path).Should().BeTrue();

    /// <summary>
    /// A build-asset mount reads straight out of the shipped assembly — no hub, no content service,
    /// no identity. This is what makes <c>/static</c> free of access control by construction.
    /// </summary>
    [Fact]
    public void BuildAssetMount_ReadsFromTheAssemblyManifest()
    {
        var mount = new StaticAssetMount("NodeTypeIcons",
            typeof(MeshNodeImageHelper).Assembly, "MeshWeaver.Graph.Icons");

        using var stream = mount.Open("box.svg");

        stream.Should().NotBeNull("the icons ship inside MeshWeaver.Graph as embedded resources");
    }

    /// <summary>A traversal never escapes a mount either.</summary>
    [Fact]
    public void BuildAssetMount_RefusesTraversal()
    {
        var mount = new StaticAssetMount("NodeTypeIcons",
            typeof(MeshNodeImageHelper).Assembly, "MeshWeaver.Graph.Icons");

        mount.Open("../Templates/NodeCopy.csx").Should().BeNull();
    }
}
