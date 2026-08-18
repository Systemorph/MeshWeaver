using System.Xml.Linq;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

public class MeshNodeImageHelperTest
{
    [Theory]
    [InlineData("Document", null)]
    [InlineData("People", null)]
    [InlineData("/images/logo.png", "/images/logo.png")]
    [InlineData("data:image/png;base64,abc", "data:image/png;base64,abc")]
    [InlineData("https://example.com/img.png", "https://example.com/img.png")]
    [InlineData("path/to/image.png", "path/to/image.png")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void GetIconAsImageUrl_ReturnsExpected(string? icon, string? expected)
    {
        MeshNodeImageHelper.GetIconForRendering(icon).Should().Be(expected);
    }

    [Theory]
    [InlineData("Markdown", "/static/NodeTypeIcons/document.svg")]
    [InlineData("Code", "/static/NodeTypeIcons/code.svg")]
    [InlineData("Agent", "/static/NodeTypeIcons/bot.svg")]
    [InlineData("Skill", "/static/NodeTypeIcons/sparkle.svg")] // skill instances read as their NodeType (sparkle)
    [InlineData("Thread", "/static/NodeTypeIcons/chat.svg")]
    [InlineData("User", "/static/NodeTypeIcons/person.svg")]
    [InlineData("Type/Code", "/static/NodeTypeIcons/code.svg")] // path form → matched on last segment
    [InlineData("SomeCustomType", "/static/NodeTypeIcons/box.svg")] // unknown → neutral box, never a letter
    public void DefaultIconForNodeType_MapsKnownTypes_AndFallsBackToBox(string nodeType, string expected)
        => MeshNodeImageHelper.DefaultIconForNodeType(nodeType).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void DefaultIconForNodeType_NullOrEmpty_ReturnsNull(string? nodeType)
        => MeshNodeImageHelper.DefaultIconForNodeType(nodeType).Should().BeNull();

    [Fact]
    public void ResolveNodeIcon_NoInstanceIcon_FallsBackToNodeTypeIcon()
    {
        var node = new MeshNode("ArbeitsanweisungenListe2", "AgenticPension") { NodeType = "Markdown" };
        MeshNodeImageHelper.ResolveNodeIcon(node).Should().Be("/static/NodeTypeIcons/document.svg");
    }

    [Fact]
    public void ResolveNodeIcon_InstanceIconWins_OverNodeTypeDefault()
    {
        var node = new MeshNode("X", "ns") { NodeType = "Markdown", Icon = "🎯" };
        MeshNodeImageHelper.ResolveNodeIcon(node).Should().Be("🎯");
    }

    [Fact]
    public void ResolveNodeIcon_TypelessNodeWithNoIcon_FallsBackToBox_NeverNull()
    {
        // A node with no icon AND no (mapped) NodeType must still resolve to an SVG so the card
        // never renders the bare-initial (blue) placeholder. This is the issue-2 guarantee.
        var node = new MeshNode("X", "ns");
        MeshNodeImageHelper.ResolveNodeIcon(node).Should().Be("/static/NodeTypeIcons/box.svg");
    }

    [Fact]
    public void SizeInlineSvg_Injects_Explicit_Size_Into_Opening_Tag()
    {
        // viewBox-only inline svgs have no intrinsic size; on raw-HTML surfaces
        // (Controls.Html tiles) no scoped CSS can reach them, so the size must
        // live in the markup — first style attribute wins in HTML parsing.
        const string svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\"><path d=\"M0 0h24v24\"/></svg>";
        var sized = MeshNodeImageHelper.SizeInlineSvg(svg, 48);
        sized.Should().StartWith("<svg style=\"width: 48px; height: 48px; display: block;\"");
        sized.Should().Contain("viewBox=\"0 0 24 24\"");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not an svg")]
    public void SizeInlineSvg_PassesThrough_NonSvg(string? value)
        => MeshNodeImageHelper.SizeInlineSvg(value!, 48).Should().Be(value);

    // ── Fluent icon NAMES resolve to the shipped glyph of the same name ────────────────────

    /// <summary>
    /// 🚨 THE BUG THIS FIXES. A Fluent name resolved to nothing, so every node carrying one fell
    /// through to its NodeType default — which is why EVERY Skill in the Store rendered the same
    /// <c>sparkle</c> no matter which icon it declared. Skills author Fluent names because that is
    /// what the nav and the chat composer render; a card built as an HTML string has no Blazor
    /// component to render one with, so it needs a URL.
    /// </summary>
    [Theory]
    [InlineData("Sparkle", "/static/NodeTypeIcons/sparkle.svg")]
    [InlineData("Presentation", "/static/NodeTypeIcons/presentation.svg")]
    [InlineData("People", "/static/NodeTypeIcons/people.svg")]
    [InlineData("Key", "/static/NodeTypeIcons/key.svg")]
    [InlineData("Bot", "/static/NodeTypeIcons/bot.svg")]
    // The names skills use that had NO shipped glyph until now — each one was a generic sparkle.
    [InlineData("Location", "/static/NodeTypeIcons/location.svg")]
    [InlineData("LockClosed", "/static/NodeTypeIcons/lockclosed.svg")]
    [InlineData("Layout", "/static/NodeTypeIcons/layout.svg")]
    [InlineData("History", "/static/NodeTypeIcons/history.svg")]
    [InlineData("DeviceMobile", "/static/NodeTypeIcons/devicemobile.svg")]
    [InlineData("Add", "/static/NodeTypeIcons/add.svg")]
    [InlineData("PuzzlePiece", "/static/NodeTypeIcons/puzzlepiece.svg")]
    [InlineData("CloudArrowUp", "/static/NodeTypeIcons/cloudarrowup.svg")]
    [InlineData("Bug", "/static/NodeTypeIcons/bug.svg")]
    public void AFluentName_ResolvesToTheShippedGlyphOfThatName(string icon, string expected)
        => MeshNodeImageHelper.ShippedIconFor(icon).Should().Be(expected);

    /// <summary>A Fluent name with no shipped glyph must NOT invent a URL — the node-type default
    /// still has to take over, or the card would 404 on an asset that was never built.</summary>
    [Fact]
    public void AFluentName_WithNoShippedGlyph_ResolvesToNothing()
        => MeshNodeImageHelper.ShippedIconFor("NoSuchIconNameAtAll").Should().BeNull();

    /// <summary>Only Fluent NAMES take this path — a URL, inline SVG or emoji is already
    /// renderable and must pass through the earlier branches untouched.</summary>
    [Theory]
    [InlineData("/static/NodeTypeIcons/code.svg")]
    [InlineData("<svg viewBox=\"0 0 20 20\"></svg>")]
    [InlineData("🧊")]
    [InlineData(null)]
    [InlineData("")]
    public void NonFluentIcons_AreNotRoutedThroughTheShippedSet(string? icon)
        => MeshNodeImageHelper.ShippedIconFor(icon).Should().BeNull();

    /// <summary>
    /// End to end on a real Skill node: the declared icon wins over the type default. Before, both
    /// of these resolved to sparkle and every skill in the Store looked identical.
    /// </summary>
    [Fact]
    public void ASkill_KeepsItsOwnIcon_InsteadOfTheGenericSparkle()
    {
        var navigate = new MeshNode("navigate", "Essentials/Skill") { NodeType = "Skill", Icon = "Location" };
        var history = new MeshNode("recap", "Essentials/Skill") { NodeType = "Skill", Icon = "History" };

        MeshNodeImageHelper.ResolveNodeIcon(navigate).Should().Be("/static/NodeTypeIcons/location.svg");
        MeshNodeImageHelper.ResolveNodeIcon(history).Should().Be("/static/NodeTypeIcons/history.svg");
    }

    /// <summary>A skill whose Fluent name has no glyph still falls back to the Skill type's
    /// sparkle — the guarantee that a card never renders a bare letter is unchanged.</summary>
    [Fact]
    public void ASkill_WithAnUnknownFluentName_StillFallsBackToItsTypeIcon()
    {
        var node = new MeshNode("x", "Essentials/Skill") { NodeType = "Skill", Icon = "SomethingUnmapped" };

        MeshNodeImageHelper.ResolveNodeIcon(node).Should().Be("/static/NodeTypeIcons/sparkle.svg");
    }

    /// <summary>
    /// The three glyphs that were REFERENCED by shipped skills but never built — they answered 404
    /// live, so those skills rendered a broken image rather than an icon.
    /// </summary>
    [Theory]
    [InlineData("book")]
    [InlineData("target")]
    [InlineData("library")]
    public void TheIconsSkillsAlreadyReference_AreActuallyShipped(string name)
    {
        var resource = $"MeshWeaver.Graph.Icons.{name}.svg";

        typeof(MeshNodeImageHelper).Assembly.GetManifestResourceNames()
            .Should().Contain(resource,
                "a skill already points at /static/NodeTypeIcons/{0}.svg — without the asset it is a broken image", name);
    }

    // ── ResolveRenderable: every icon form gets the element it needs, never a broken <img> ────

    /// <summary>The standard glyph a thread surface passes as the fallback.</summary>
    private const string ThreadIcon = "/static/NodeTypeIcons/chat.svg";

    /// <summary>
    /// 🚨 THE BUG THIS FIXES. Every thread node carries a <c>ThreadIconGenerator</c> identicon —
    /// raw inline <c>&lt;svg&gt;</c>, not a URL — and the delegated-sub-thread chip put it straight
    /// into <c>&lt;img src="…"&gt;</c>, so the browser drew its broken-image glyph. Inline SVG must
    /// classify as markup, and the value must survive verbatim.
    /// </summary>
    [Fact]
    public void AnInlineSvgIcon_RendersAsMarkup_NeverAsAnImageSource()
    {
        const string identicon =
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 80 80\"><rect width=\"80\" height=\"80\" rx=\"16\" fill=\"#eae5f5\"/></svg>";

        var resolved = MeshNodeImageHelper.ResolveRenderable(identicon, ThreadIcon);

        resolved.Kind.Should().Be(IconRenderKind.InlineSvg);
        resolved.Value.Should().Be(identicon);
    }

    [Theory]
    [InlineData("/static/NodeTypeIcons/bot.svg")]
    [InlineData("https://example.com/avatar.png")]
    [InlineData("data:image/png;base64,abc")]
    public void AUrlIcon_RendersAsAnImage(string icon)
        => MeshNodeImageHelper.ResolveRenderable(icon, ThreadIcon)
            .Should().Be(new RenderableIcon(IconRenderKind.Image, icon));

    [Theory]
    [InlineData("🎯")]
    [InlineData("➕")]
    public void AnEmojiIcon_RendersAsText(string icon)
        => MeshNodeImageHelper.ResolveRenderable(icon, ThreadIcon)
            .Should().Be(new RenderableIcon(IconRenderKind.Glyph, icon));

    /// <summary>A legacy Fluent NAME is renderable only where a glyph of that name is shipped.</summary>
    [Fact]
    public void AFluentName_RendersAsItsShippedGlyph()
        => MeshNodeImageHelper.ResolveRenderable("Sparkle", ThreadIcon)
            .Should().Be(new RenderableIcon(IconRenderKind.Image, "/static/NodeTypeIcons/sparkle.svg"));

    /// <summary>
    /// The other half of the bug: a Fluent name with no shipped glyph used to become
    /// <c>&lt;img src="DocumentPdf"&gt;</c>. It has to yield the caller's standard icon instead.
    /// </summary>
    [Fact]
    public void AFluentName_WithNoShippedGlyph_FallsBackToTheStandardIcon()
        => MeshNodeImageHelper.ResolveRenderable("NoSuchIconNameAtAll", ThreadIcon)
            .Should().Be(new RenderableIcon(IconRenderKind.Image, ThreadIcon));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoIcon_FallsBackToTheStandardIcon(string? icon)
        => MeshNodeImageHelper.ResolveRenderable(icon, ThreadIcon)
            .Should().Be(new RenderableIcon(IconRenderKind.Image, ThreadIcon));

    /// <summary>
    /// Total, so no caller can produce a broken icon: with neither a usable icon NOR a usable
    /// fallback, the neutral box glyph still comes out.
    /// </summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("NoSuchIconNameAtAll", "AlsoNotAnIcon")]
    public void NeitherIconNorFallback_StillYieldsTheNeutralGlyph(string? icon, string? fallback)
        => MeshNodeImageHelper.ResolveRenderable(icon, fallback)
            .Should().Be(new RenderableIcon(IconRenderKind.Image, "/static/NodeTypeIcons/box.svg"));

    /// <summary>
    /// The browser tab is a <c>&lt;link rel="icon"&gt;</c>, so every icon form has to come out as
    /// something an <c>href</c> can carry — otherwise the tab silently keeps the site-wide favicon
    /// and a tab strip full of pages stays unreadable. A URL travels as itself, with the media type
    /// its extension pins down.
    /// </summary>
    [Theory]
    [InlineData("/static/NodeTypeIcons/document.svg", "image/svg+xml")]
    [InlineData("/api/content/ACME/Space/content/logo.png", "image/png")]
    [InlineData("/api/content/ACME/Space/content/logo.PNG", "image/png")]
    [InlineData("/api/content/ACME/Space/content/cover.jpeg", "image/jpeg")]
    [InlineData("https://example.com/mark.svg", "image/svg+xml")]
    [InlineData("data:image/png;base64,abc", "image/png")]
    [InlineData("/api/content/ACME/Space/content/logo.svg?v=3", "image/svg+xml")]
    public void IconLinkFor_AUrl_TravelsAsItself_WithItsOwnMediaType(string icon, string expectedType)
        => MeshNodeImageHelper.IconLinkFor(icon)
            .Should().Be(new IconLink(icon, expectedType));

    /// <summary>
    /// A type we cannot pin down is declared as NOTHING rather than guessed at — a browser ranking
    /// several icons by type must never be told a wrong one.
    /// </summary>
    [Theory]
    [InlineData("/api/content/ACME/Space/content/mark")]
    [InlineData("/api/content/ACME/Space/content/mark.weird")]
    // A dotted DIRECTORY is not an extension — reading one as ".Space" would declare a made-up type.
    [InlineData("/api/content/ACME/My.Space/content/mark")]
    public void IconLinkFor_AnUnknownExtension_DeclaresNoType(string icon)
        => MeshNodeImageHelper.IconLinkFor(icon)
            .Should().Be(new IconLink(icon, null));

    /// <summary>
    /// Inline <c>&lt;svg&gt;</c> is MARKUP, not a location — every thread carries one
    /// (<c>ThreadIconGenerator</c>) — so it has to become a data URI. An <c>href</c> pointing at raw
    /// svg text is exactly the broken-image case <see cref="MeshNodeImageHelper.ResolveRenderable"/>
    /// exists to prevent.
    /// </summary>
    [Fact]
    public void IconLinkFor_InlineSvg_BecomesADataUri()
    {
        const string svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 20 20\"></svg>";

        var link = MeshNodeImageHelper.IconLinkFor(svg);

        link.Type.Should().Be("image/svg+xml");
        link.Href.Should().StartWith("data:image/svg+xml,");
        Uri.UnescapeDataString(link.Href["data:image/svg+xml,".Length..])
            .Should().Be(svg, "the node's icon travels byte for byte — only its transport changes");
    }

    /// <summary>
    /// A text glyph cannot be an <c>href</c> either, so it is drawn INTO an svg. Without this an
    /// emoji-iconed node fell back to the portal favicon, which is the very sameness this fixes.
    /// </summary>
    [Theory]
    [InlineData("🎯")]
    [InlineData("➕")]
    public void IconLinkFor_AGlyph_IsDrawnIntoAnSvgDataUri(string glyph)
    {
        var link = MeshNodeImageHelper.IconLinkFor(glyph);

        link.Type.Should().Be("image/svg+xml");
        var svg = Uri.UnescapeDataString(link.Href["data:image/svg+xml,".Length..]);
        svg.Should().StartWith("<svg").And.EndWith("</svg>").And.Contain(glyph);
    }

    /// <summary>Only the first grapheme cluster is drawn: an icon field holding a word would
    /// otherwise run off the canvas, and a multi-codepoint emoji must stay ONE glyph.</summary>
    [Theory]
    [InlineData("R&D", "R")]
    [InlineData("hello", "h")]
    [InlineData("👨‍👩‍👧‍👦", "👨‍👩‍👧‍👦")]
    public void IconLinkFor_AGlyph_DrawsOneClusterAndEscapesIt(string glyph, string expected)
    {
        var svg = Uri.UnescapeDataString(
            MeshNodeImageHelper.IconLinkFor(glyph).Href["data:image/svg+xml,".Length..]);

        // The markup must be well-formed even for a glyph containing XML characters.
        XDocument.Parse(svg).Root!.Value.Should().Be(expected);
    }

    /// <summary>
    /// The tab shows what the app shows: <see cref="MeshNodeImageHelper.ResolveIconLink"/> is
    /// <see cref="MeshNodeImageHelper.ResolveNodeIcon"/>, so a node with no icon of its own reads as
    /// its NodeType rather than falling back to the portal favicon.
    /// </summary>
    [Fact]
    public void ResolveIconLink_NoOwnIcon_UsesTheNodeTypeGlyph()
        => MeshNodeImageHelper.ResolveIconLink(new MeshNode("Page", "ACME") { NodeType = "Markdown" })
            .Should().Be(new IconLink("/static/NodeTypeIcons/document.svg", "image/svg+xml"));

    /// <summary>A typeless node still resolves — total, so a tab never keeps the PREVIOUS page's
    /// icon after navigating.</summary>
    [Fact]
    public void ResolveIconLink_IsTotal_EvenForATypelessIconlessNode()
        => MeshNodeImageHelper.ResolveIconLink(new MeshNode("Page", "ACME"))
            .Should().Be(new IconLink("/static/NodeTypeIcons/box.svg", "image/svg+xml"));

    /// <summary>A <c>content:</c> icon resolves through the ACCESS-CONTROLLED content route (issue
    /// #587), never <c>/static/storage</c> — the tab must not be a way around a partition's policy.</summary>
    [Fact]
    public void ResolveIconLink_AContentIcon_UsesTheAccessControlledRoute()
    {
        var node = new MeshNode("Space", "ACME") { NodeType = "Space", Icon = "content:logo.svg" };

        var link = MeshNodeImageHelper.ResolveIconLink(node);

        link.Type.Should().Be("image/svg+xml");
        link.Href.Should().StartWith("/api/content/").And.EndWith("logo.svg");
        link.Href.Should().NotContain("/static/storage");
    }
}
