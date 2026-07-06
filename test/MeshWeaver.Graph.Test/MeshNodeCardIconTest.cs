using System.Text.Json;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins that an inline <c>&lt;svg&gt;</c> icon flows through <see cref="MeshNodeThumbnailControl.GetImageUrlForNode"/>
/// verbatim (never rewritten to a <c>/static/…</c> path) — from the node's top-level <c>Icon</c>, from a content
/// <c>avatar</c>/<c>logo</c>, and from a content-level <c>icon</c> property. The card renderers (Blazor
/// MeshNodeCardView and the React MeshNodeCardView) detect the leading <c>&lt;svg</c> and render it inline.
/// </summary>
public class MeshNodeCardIconTest
{
    private const string Svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\"><path d=\"M0 0h24v24\"/></svg>";

    [Fact]
    public void TopLevelIcon_InlineSvg_ReturnedVerbatim()
    {
        var node = new MeshNode("X", "ns") { NodeType = "Markdown", Icon = Svg };
        MeshNodeThumbnailControl.GetImageUrlForNode(node).Should().Be(Svg);
    }

    [Fact]
    public void ContentAvatar_InlineSvg_ReturnedVerbatim()
    {
        var node = new MeshNode("X", "ns")
        {
            NodeType = "Markdown",
            Content = new Dictionary<string, object> { ["avatar"] = Svg },
        };
        MeshNodeThumbnailControl.GetImageUrlForNode(node).Should().Be(Svg);
    }

    [Fact]
    public void ContentIcon_Dictionary_InlineSvg_ReturnedVerbatim()
    {
        var node = new MeshNode("X", "ns")
        {
            NodeType = "Markdown",
            Content = new Dictionary<string, object> { ["icon"] = Svg },
        };
        MeshNodeThumbnailControl.GetImageUrlForNode(node).Should().Be(Svg);
    }

    [Fact]
    public void ContentIcon_JsonElement_InlineSvg_ReturnedVerbatim()
    {
        // A degraded JSON frame (cache / change-feed read) carrying the icon in its content —
        // previously dropped in favour of the generic NodeType default; now surfaced verbatim.
        var content = JsonSerializer.Deserialize<JsonElement>($"{{\"icon\": {JsonSerializer.Serialize(Svg)}}}");
        var node = new MeshNode("X", "ns") { NodeType = "Markdown", Content = content };
        MeshNodeThumbnailControl.GetImageUrlForNode(node).Should().Be(Svg);
    }

    [Fact]
    public void ContentIcon_FluentName_FallsThroughToNodeTypeDefault()
    {
        // A legacy Fluent icon NAME in content is not renderable markup — it must fall through to the
        // NodeType default rather than be surfaced as-is.
        var node = new MeshNode("X", "ns")
        {
            NodeType = "Markdown",
            Content = new Dictionary<string, object> { ["icon"] = "Document" },
        };
        MeshNodeThumbnailControl.GetImageUrlForNode(node).Should().Be("/static/NodeTypeIcons/document.svg");
    }
}
