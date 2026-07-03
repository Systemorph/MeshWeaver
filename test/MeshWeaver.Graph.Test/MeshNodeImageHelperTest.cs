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
}
