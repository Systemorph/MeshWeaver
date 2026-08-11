using Memex.Portal.Shared.Seo;
using MeshWeaver.Mesh;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Pins <see cref="SeoResolver.ResolveIcon"/> — the page-icon channel of the SEO head.
///
/// <para>🚨 THE DEFECT: every page of a Blazor portal serves ONE site-wide
/// <c>&lt;link rel="icon"&gt;</c>, so every link preview of every node — the portal's own
/// <c>OgCard</c> grid, and equally a Slack / Teams / LinkedIn unfurl — drew the same MeshWeaver
/// logo, even though each node already carries a distinctive icon that the app renders everywhere
/// internally.</para>
///
/// <para>The rule these tests pin is deliberately narrow: it is THE ICON ON THE NODE, and nothing
/// else. No synthesised badge, no letter tile, no NodeType stand-in — a node that carries no icon
/// of its own keeps the portal favicon, which is the honest answer for a page with no mark.</para>
/// </summary>
public class SeoResolverIconTest
{
    private static MeshNode Node(string? icon, string id = "Ifrs17", string? ns = null) =>
        new(id, ns) { NodeType = "Store/Plugin", Icon = icon };

    /// <summary>
    /// The real shape the failing cards had: an inline <c>&lt;svg&gt;</c> on the node. It is markup,
    /// not a location, so it travels as a data URI — and the svg inside it is the node's icon
    /// UNCHANGED, byte for byte.
    /// </summary>
    [Fact]
    public void InlineSvgIcon_BecomesADataUriCarryingThatExactSvg()
    {
        const string svg = "<svg viewBox='0 0 24 24'><rect width='24' height='24' fill='#4c1d95'/></svg>";

        var icon = SeoResolver.ResolveIcon(Node(svg));

        Assert.NotNull(icon);
        Assert.Equal("image/svg+xml", icon.Type);
        Assert.StartsWith("data:image/svg+xml,", icon.Href);
        Assert.Equal(svg, Uri.UnescapeDataString(icon.Href["data:image/svg+xml,".Length..]));
    }

    /// <summary>A <c>content:</c> reference resolves the SAME way the in-app icon does — through
    /// the access-controlled content route, never <c>/static</c>.</summary>
    [Fact]
    public void ContentReferenceIcon_ResolvesToTheAccessControlledContentUrl()
    {
        var icon = SeoResolver.ResolveIcon(Node("content:mark.svg", id: "Page", ns: "Space"));

        Assert.NotNull(icon);
        Assert.Contains("mark.svg", icon.Href);
        Assert.DoesNotContain("/static/", icon.Href);
        Assert.Equal("image/svg+xml", icon.Type);
    }

    /// <summary>A URL the node carries is used as written; the media type is declared only when the
    /// value pins one down, because a consumer that ranks icons by type must not be told a lie.</summary>
    [Fact]
    public void UrlIcon_UsedAsWritten_TypeOnlyWhenTheValuePinsItDown()
    {
        var png = SeoResolver.ResolveIcon(Node("https://cdn.example.org/mark.png"));
        Assert.NotNull(png);
        Assert.Equal("https://cdn.example.org/mark.png", png.Href);
        Assert.Null(png.Type);

        var svg = SeoResolver.ResolveIcon(Node("https://cdn.example.org/mark.svg"));
        Assert.Equal("image/svg+xml", svg!.Type);

        var dataPng = SeoResolver.ResolveIcon(Node("data:image/png;base64,iVBORw0KGgo="));
        Assert.Null(dataPng!.Type);
    }

    /// <summary>
    /// 🚨 Nothing is invented. A node with no icon, an emoji, or a legacy Fluent icon NAME yields
    /// no page icon at all — the portal favicon stays. Emitting a drawn badge or a NodeType glyph
    /// here would put a picture in the head that the node never chose.
    /// </summary>
    [Fact]
    public void NoIconOfItsOwn_YieldsNothing_SoThePortalFaviconStays()
    {
        Assert.Null(SeoResolver.ResolveIcon(Node(null)));
        Assert.Null(SeoResolver.ResolveIcon(Node("")));
        Assert.Null(SeoResolver.ResolveIcon(Node("   ")));
        // An emoji is a character, not a picture — no href can carry it.
        Assert.Null(SeoResolver.ResolveIcon(Node("📊")));
        // A legacy Fluent icon name is a component reference, not a location.
        Assert.Null(SeoResolver.ResolveIcon(Node("Document")));
    }
}
