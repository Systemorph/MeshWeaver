using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Package-root icon inheritance — issue #2075, item 2.
///
/// <para>A lesson under <c>AgenticEngineering</c>, a game under <c>Chess</c>, a doc under a store
/// package: none authors an icon of its own, so every one of them resolved to the same generic
/// <c>document</c> / <c>box</c> chrome, in the page header and in the browser tab. The package roots
/// DO carry marks. These tests pin the step that reaches for one, and — just as importantly — the
/// three places it must NOT reach.</para>
/// </summary>
public class PackageRootIconInheritanceTest
{
    private const string Package = "Chess";

    /// <summary>An authored package mark: inline svg with a full-bleed plate, the shape every store
    /// mark was aligned to so it reads at 16 px (MeshWeaver.Plugins #588).</summary>
    private const string PackageMark =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\">"
        + "<rect width=\"24\" height=\"24\" rx=\"5\" fill=\"#1f6feb\"/>"
        + "<path d=\"M8 6h8v3H8z\" fill=\"#fff\"/></svg>";

    private const string DocumentGlyph = "/static/NodeTypeIcons/document.svg";
    private const string NeutralBox = "/static/NodeTypeIcons/box.svg";

    private static MeshNode Root(string? icon, string? nodeType = "Space") =>
        new(Package) { Name = "Chess", NodeType = nodeType, Icon = icon };

    private static MeshNode Child(string? icon = null, string? nodeType = "Markdown") =>
        new("Rules", Package) { Name = "Rules", NodeType = nodeType, Icon = icon };

    // ── The path arithmetic ───────────────────────────────────────────────────────────────────

    /// <summary>The partition root is the FIRST segment, not the parent — a doc three levels down a
    /// package still wears the package's mark, not its folder's.</summary>
    [Theory]
    [InlineData("Chess/Rules", "Chess")]
    [InlineData("Doc/Architecture/LinkPreviews", "Doc")]
    [InlineData("AgenticEngineering/Lessons/01-TheMagicWish", "AgenticEngineering")]
    [InlineData("/Chess/Rules", "Chess")] // leading separator tolerated
    public void PartitionRootPath_IsTheFirstSegment(string nodePath, string expected)
        => MeshNodeImageHelper.PartitionRootPath(nodePath).Should().Be(expected);

    /// <summary>Null exactly where there is nothing above to inherit from — which is what makes a
    /// partition root structurally unable to resolve itself.</summary>
    [Theory]
    [InlineData("Chess")]
    [InlineData("/Chess/")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void PartitionRootPath_NullWhenThereIsNoDistinctRoot(string? nodePath)
        => MeshNodeImageHelper.PartitionRootPath(nodePath).Should().BeNull();

    // ── The inheritance step ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 THE FEATURE. A child with no icon of its own wears the package's mark instead of the
    /// generic glyph for its type — and the one-argument overload still resolves the old way, so
    /// nothing that has not opted in can change under it.
    /// </summary>
    [Fact]
    public void AChildWithNoIconOfItsOwn_InheritsThePackageMark()
    {
        var child = Child();

        MeshNodeImageHelper.ResolveNodeIcon(child).Should().Be(DocumentGlyph);
        MeshNodeImageHelper.ResolveNodeIcon(child, Root(PackageMark)).Should().Be(PackageMark);
    }

    /// <summary>A node's OWN icon always wins — inheritance sits BELOW it in the chain, so marking a
    /// package can never overwrite what a page chose for itself.</summary>
    [Fact]
    public void AnOwnIcon_WinsOverThePackageMark()
        => MeshNodeImageHelper.ResolveNodeIcon(Child("🎯"), Root(PackageMark)).Should().Be("🎯");

    /// <summary>A package root does not inherit: its own path has no first-segment ANCESTOR, so
    /// handing it itself changes nothing and cannot loop.</summary>
    [Fact]
    public void APartitionRoot_DoesNotInheritFromItself()
    {
        var root = Root(icon: null, nodeType: "Markdown");

        MeshNodeImageHelper.ResolveNodeIcon(root, root).Should().Be(DocumentGlyph);
        MeshNodeImageHelper.InheritedIcon(root, root).Should().BeNull();
    }

    /// <summary>An UNMARKED package changes nothing: the chain falls through to the NodeType default
    /// exactly as before, so adding the step cannot regress a package that never had a mark.</summary>
    [Fact]
    public void AnUnmarkedPackageRoot_FallsThroughToTheNodeTypeDefault()
        => MeshNodeImageHelper.ResolveNodeIcon(Child(), Root(icon: null)).Should().Be(DocumentGlyph);

    /// <summary>…and the chain stays TOTAL underneath that: a typeless child under an unmarked
    /// package still reaches the neutral box, never null and never a bare initial.</summary>
    [Fact]
    public void AnUnmarkedPackageRoot_ATypelessChild_StillReachesTheNeutralBox()
        => MeshNodeImageHelper.ResolveNodeIcon(Child(nodeType: null), Root(icon: null))
            .Should().Be(NeutralBox);

    /// <summary>
    /// 🚨 ONLY THE ROOT'S OWN MARK IS INHERITED — never the root's own fallback chain. An unmarked
    /// <c>Space</c> root resolves to the <c>organization</c> glyph for ITSELF; borrowing that would
    /// dress every document in the package as an organization, which is strictly worse than the
    /// document glyph it replaced.
    /// </summary>
    [Fact]
    public void TheRootsOwnNodeTypeDefault_IsNotInherited()
    {
        var root = Root(icon: null);

        // The root resolves to its own type glyph...
        MeshNodeImageHelper.ResolveNodeIcon(root).Should().Be("/static/NodeTypeIcons/organization.svg");
        // ...and the child does NOT get it.
        MeshNodeImageHelper.ResolveNodeIcon(Child(), root).Should().Be(DocumentGlyph);
    }

    /// <summary>
    /// 🚨 THE SUPPLIED ROOT IS VERIFIED, NOT TRUSTED. A caller that hands over the wrong node — a
    /// stale frame, the parent instead of the root — gets no inheritance rather than an unrelated
    /// package's mark on someone else's page, which is the failure a screenshot could not catch.
    /// </summary>
    [Fact]
    public void ANodeThatIsNotThisNodesPartitionRoot_IsIgnored()
    {
        var otherPackage = new MeshNode("Draughts") { NodeType = "Space", Icon = PackageMark };
        var intermediateFolder = new MeshNode("Lessons", Package) { Icon = PackageMark };

        MeshNodeImageHelper.ResolveNodeIcon(Child(), otherPackage).Should().Be(DocumentGlyph);
        MeshNodeImageHelper.ResolveNodeIcon(Child(), intermediateFolder).Should().Be(DocumentGlyph);
    }

    /// <summary>A null root is simply the un-inherited resolution — the one-argument overload's
    /// behaviour, reached by every call site that has not opted in.</summary>
    [Fact]
    public void ANullRoot_ResolvesExactlyAsTheOneArgumentOverloadDoes()
        => MeshNodeImageHelper.ResolveNodeIcon(Child(), null)
            .Should().Be(MeshNodeImageHelper.ResolveNodeIcon(Child()));

    /// <summary>A root marked with a <c>content:</c> reference is inherited through the
    /// ACCESS-CONTROLLED content route (issue #587) and resolved against the ROOT's path — never the
    /// child's, which holds no such file.</summary>
    [Fact]
    public void AContentReferenceMark_ResolvesAgainstTheRootsOwnContentCollection()
    {
        var icon = MeshNodeImageHelper.ResolveNodeIcon(Child(), Root("content:mark.svg"));

        icon.Should().StartWith("/api/content/").And.EndWith("mark.svg");
        icon.Should().Contain(Package).And.NotContain("Rules");
        icon.Should().NotContain("/static/storage");
    }

    /// <summary>A root marked with a shipped glyph NAME resolves to that glyph's URL — the same
    /// second step a node's own Fluent-named icon gets, so authoring a name on a package works the
    /// way authoring one on a page does.</summary>
    [Fact]
    public void AShippedGlyphNameOnTheRoot_ResolvesToThatGlyph()
        => MeshNodeImageHelper.ResolveNodeIcon(Child(), Root("Sparkle"))
            .Should().Be("/static/NodeTypeIcons/sparkle.svg");

    /// <summary>A root whose icon is a Fluent name this assembly ships no glyph for cannot be
    /// rendered, so it is not inherited — the child falls through rather than carrying a value that
    /// would render as a broken image.</summary>
    [Fact]
    public void AnUnrenderableFluentNameOnTheRoot_IsNotInherited()
        => MeshNodeImageHelper.ResolveNodeIcon(Child(), Root("NoSuchIconNameAtAll"))
            .Should().Be(DocumentGlyph);

    // ── The browser tab ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The tab is the surface #2075 was reported on: at 16 px "which package is this" is the only
    /// thing worth saying, and <c>document</c> says nothing. Resolution is the same chain the app
    /// renders, so the tab and the page cannot disagree about an inherited mark either.
    /// </summary>
    [Fact]
    public void ResolveIconLink_InheritsThePackageMarkIntoTheTab()
    {
        var withoutRoot = MeshNodeImageHelper.ResolveIconLink(Child());
        var withRoot = MeshNodeImageHelper.ResolveIconLink(Child(), Root(PackageMark));

        withoutRoot.Href.Should().Be(DocumentGlyph);
        withRoot.Type.Should().Be(MeshNodeImageHelper.SvgMediaType);
        Uri.UnescapeDataString(withRoot.Href["data:image/svg+xml,".Length..]).Should().Be(PackageMark);
    }

    /// <summary>An inherited mark that is an OFFICIAL third-party mark still yields the portal's own
    /// mark in the tab — a favicon claims the tab IS the site's, and that claim does not become
    /// truer by being inherited.</summary>
    [Fact]
    public void ResolveIconLink_AnInheritedOfficialMark_StillYieldsThePortalMark()
    {
        var official = "<svg xmlns=\"http://www.w3.org/2000/svg\" "
                       + $"{IconBackplate.OfficialMarkAttribute}=\"{IconBackplate.OfficialMarkValue}\""
                       + " viewBox=\"0 0 24 24\"><rect width=\"24\" height=\"24\" fill=\"#111827\"/></svg>";

        // Precondition: the value really is classified as an official mark.
        MeshNodeImageHelper.IsOfficialMark(official).Should().BeTrue();

        MeshNodeImageHelper.ResolveIconLink(Child(), Root(official))
            .Href.Should().Contain(MeshNodeImageHelper.MeshWeaverMarkUrl);
    }
}
