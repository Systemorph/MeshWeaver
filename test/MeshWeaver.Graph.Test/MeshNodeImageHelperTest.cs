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
}
