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
}
