using System.Linq;
using Memex.Portal.Shared.Seo;
using MeshWeaver.Mesh;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The set of <c>&lt;link&gt;</c> elements a node page's head should carry
/// (<see cref="SeoResolver.ResolveIconLinks"/>).
///
/// <para>🚨 THE DEFECT (issue #2075, item 3): the head declared exactly one icon — the node's own
/// mark as an <c>image/svg+xml</c> data URI — and <b>Safari renders no SVG favicon at all</b>. So
/// on every Mac and iPhone the per-content favicon was invisible: each tab wore the portal mark,
/// in and out of circuit. The cure is not to give up the scalable icon but to declare BOTH and let
/// each browser take the one it can read.</para>
/// </summary>
public class SeoIconLinksTest
{
    private const string AuthoredMark =
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 48 48'>"
        + "<rect width='48' height='48' rx='10' fill='#f0d9b5'/></svg>";

    private static MeshNode Node(string? icon, string path = "Chess") =>
        new(path) { NodeType = "Store/Plugin", Icon = icon };

    /// <summary>
    /// An svg mark yields three links: the scalable icon (unchanged, so a browser that reads SVG
    /// still gets the crisp one), the PNG favicon Safari will actually take, and the
    /// <c>apple-touch-icon</c> — a SEPARATE channel that Safari does not fall back to the favicon
    /// for, so with none declared it draws its own letter tile instead of the node's mark.
    /// </summary>
    [Fact]
    public void AnSvgMark_DeclaresTheScalableIcon_APngFallback_AndAnAppleTouchIcon()
    {
        var links = SeoResolver.ResolveIconLinks(Node(AuthoredMark));

        Assert.Equal(3, links.Count);

        var svg = links[0];
        Assert.Equal("icon", svg.Rel);
        Assert.Equal("image/svg+xml", svg.Type);
        Assert.StartsWith("data:image/svg+xml,", svg.Href);
        // A scalable icon declares no size: telling a browser that ranks icons by size that this
        // one is 32 px would be a lie about the only thing that makes it worth preferring.
        Assert.Null(svg.Sizes);

        var png = links[1];
        Assert.Equal("icon", png.Rel);
        Assert.Equal("image/png", png.Type);
        Assert.Equal("/api/icon/Chess.png?size=32", png.Href);
        Assert.Equal("32x32", png.Sizes);

        var touch = links[2];
        Assert.Equal("apple-touch-icon", touch.Rel);
        Assert.Equal("image/png", touch.Type);
        Assert.Equal("/api/icon/Chess.png?size=180", touch.Href);
        Assert.Equal("180x180", touch.Sizes);
    }

    /// <summary>
    /// 🚨 The raster links point at the SAME svg the first link carries. One resolution
    /// (<see cref="SeoResolver.ResolveIconSvg"/>) feeds both, so the tab's PNG and the head's data
    /// URI can never become pictures of different things.
    /// </summary>
    [Fact]
    public void ThePngAndTheDataUri_AreTheSameMark()
    {
        var node = Node(AuthoredMark);

        var fromLink = System.Uri.UnescapeDataString(
            SeoResolver.ResolveIconLinks(node)[0].Href["data:image/svg+xml,".Length..]);

        Assert.Equal(AuthoredMark, fromLink);
        Assert.Equal(AuthoredMark, SeoResolver.ResolveIconSvg(node));
    }

    /// <summary>A nested page keeps its full mesh path in the icon URL — publishing moves nothing,
    /// and the route is a catch-all, so no separator is escaped.</summary>
    [Fact]
    public void ANestedNode_KeepsItsFullPath_Unescaped()
    {
        var links = SeoResolver.ResolveIconLinks(Node(AuthoredMark, "AgenticPrimer/01-TheMagicWish"));

        Assert.Equal("/api/icon/AgenticPrimer/01-TheMagicWish.png?size=32", links[1].Href);
    }

    /// <summary>A mark that is already a raster image needs no PNG fallback — Safari reads it — so
    /// the head declares the one link it always did.</summary>
    [Fact]
    public void ARasterMark_StillDeclaresExactlyOneLink()
    {
        var links = SeoResolver.ResolveIconLinks(Node("https://cdn.example.org/mark.png"));

        var only = Assert.Single(links);
        Assert.Equal("https://cdn.example.org/mark.png", only.Href);
        Assert.Equal("icon", only.Rel);
    }

    /// <summary>
    /// 🚨 Nothing is synthesised, exactly as before. A node with no mark of its own declares NO
    /// icon links, so the portal favicon stays — the honest answer for a page with no mark, and the
    /// reason the raster route's 404 is never reached from a page we serve.
    /// </summary>
    [Fact]
    public void NoMarkOfItsOwn_DeclaresNothing()
    {
        Assert.Empty(SeoResolver.ResolveIconLinks(Node(null)));
        Assert.Empty(SeoResolver.ResolveIconLinks(Node("")));
        Assert.Empty(SeoResolver.ResolveIconLinks(Node("Document")));
        Assert.Empty(SeoResolver.ResolveIconLinks(Node("📊")));
    }

    /// <summary>
    /// The single-icon accessor the head has used since 2026-08-11 is unchanged — this change ADDS
    /// links, it does not move the existing one.
    /// </summary>
    [Fact]
    public void TheExistingSingleIconAccessor_StillAnswersTheSame()
    {
        var icon = SeoResolver.ResolveIcon(Node(AuthoredMark));

        Assert.NotNull(icon);
        Assert.Equal("image/svg+xml", icon.Type);
        Assert.Equal(AuthoredMark, System.Uri.UnescapeDataString(icon.Href["data:image/svg+xml,".Length..]));
        Assert.Equal(SeoResolver.ResolveIconLinks(Node(AuthoredMark)).First(), icon);
    }
}
