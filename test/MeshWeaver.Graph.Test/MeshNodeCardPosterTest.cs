using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins the split between the two card SHAPES: <see cref="MeshNodeCardControl"/> is an
/// ICON-beside-text card (a fixed 48 px square image box with <c>object-fit: cover</c>), while
/// <see cref="MeshNodeThumbnailControl"/> is the poster-shaped tile. A wide
/// <see cref="MarkdownContent.Thumbnail"/> banner cropped into 48 px is a meaningless sliver, so
/// the card skips it and falls through to the node's own icon — drawn for exactly that box. An
/// explicitly authored content avatar/logo/icon still wins in BOTH shapes
/// (<see cref="MeshNodeCardIconTest"/>).
/// </summary>
public class MeshNodeCardPosterTest
{
    private const string InlineSvg =
        "<svg viewBox=\"0 0 24 24\"><path fill=\"currentColor\" d=\"M4 4h16v16H4z\"/></svg>";

    private const string Poster = "/posters/wide-banner.png";

    private static MeshNode DocWithPosterAndIcon() =>
        new("Doc", "Space")
        {
            Name = "A Document",
            NodeType = "Markdown",
            Icon = InlineSvg,
            Content = new MarkdownContent { Content = "# Body", Thumbnail = Poster },
        };

    [Fact]
    public void Card_RendersNodeIcon_NotTheThumbnailPoster()
    {
        var node = DocWithPosterAndIcon();

        var card = MeshNodeCardControl.FromNode(node, node.Path);

        card.ImageUrl.Should().Be(InlineSvg, "the card's 48 px box shows the node's icon");
        card.ImageUrl.Should().NotContain("wide-banner");
    }

    [Fact]
    public void Thumbnail_KeepsThePoster()
    {
        var node = DocWithPosterAndIcon();

        MeshNodeThumbnailControl.FromNode(node, node.Path).ImageUrl.Should().Be(Poster,
            "a thumbnail tile is poster-shaped and keeps the banner");
    }

    /// <summary>A node with no thumbnail at all was ALREADY icon-driven — dropping the poster from
    /// the card chain must not disturb it.</summary>
    [Fact]
    public void Card_WithoutThumbnail_StillRendersTheIcon()
    {
        var node = new MeshNode("Plain", "Space") { Name = "Plain", Icon = InlineSvg };

        MeshNodeCardControl.FromNode(node, node.Path).ImageUrl.Should().Be(InlineSvg);
    }

    /// <summary>Every non-null node resolves to SOME icon (own icon → shipped glyph → NodeType
    /// default → neutral box), so dropping the poster can never leave a card blank.</summary>
    [Fact]
    public void Card_WithoutIconOrThumbnail_StillResolvesAVisual()
    {
        var node = new MeshNode("Typed", "Space") { Name = "Typed", NodeType = "Markdown" };

        MeshNodeCardControl.FromNode(node, node.Path).ImageUrl.Should().NotBeNullOrEmpty();
    }
}
