using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Memex.Portal.Shared.Api;
using Memex.Portal.Shared.Seo;
using MeshWeaver.Mesh;
using SkiaSharp;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The fallback share card. These assert the properties a crawler and a CDN actually depend on —
/// it is a real PNG, it is exactly 1200×630, and the same node always renders byte-identically so
/// the endpoint's strong ETag is meaningful.
/// </summary>
public class OgCardRendererTest
{
    private static OgCardRenderer NewRenderer() => new("Memex");

    /// <summary>PNG magic + IHDR width/height, read straight out of the bytes.</summary>
    private static (int Width, int Height) PngSize(byte[] png)
    {
        Assert.True(png.Length > 24, "a card that short cannot be a PNG");
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png.Take(4).ToArray());
        int Be(int at) => (png[at] << 24) | (png[at + 1] << 16) | (png[at + 2] << 8) | png[at + 3];
        return (Be(16), Be(20));
    }

    [Fact]
    public void Card_IsA1200x630Png()
    {
        using var renderer = NewRenderer();

        var png = renderer.Render(
            "Claims", "Claims, moved to the age of agents.", "Insurance", "Claims");

        var (width, height) = PngSize(png);
        Assert.Equal(1200, width);
        Assert.Equal(630, height);
    }

    /// <summary>
    /// The endpoint serves a strong ETag computed from these bytes, so an unchanged node must
    /// render identically — otherwise every crawler refetch is a cache miss and the 304 path is
    /// dead code.
    /// </summary>
    [Fact]
    public void SameNode_RendersByteIdentically()
    {
        using var renderer = NewRenderer();

        var first = renderer.Render("Underwriting", "The governed workflow.", "Insurance", "Underwriting");
        var second = renderer.Render("Underwriting", "The governed workflow.", "Insurance", "Underwriting");

        Assert.Equal(first, second);
    }

    /// <summary>A renamed node must produce a different card, or the ETag would serve the old one
    /// forever.</summary>
    [Fact]
    public void ADifferentTitle_ChangesTheBytes()
    {
        using var renderer = NewRenderer();

        var before = renderer.Render("Claims", "Same description.", "Insurance", "Claims");
        var after = renderer.Render("Claims Deepfield", "Same description.", "Insurance", "Claims");

        Assert.NotEqual(before, after);
    }

    /// <summary>
    /// The inputs that actually occur in the mesh: no description, no category, a title long
    /// enough to need shrinking, and a single unbreakable token. None may throw or produce a
    /// degenerate image.
    /// </summary>
    [Theory]
    [InlineData("Claims", null, null)]
    [InlineData("Claims", "", "")]
    [InlineData(
        "A course about business rules that explain themselves to auditors, accountants and regulators alike",
        "Business logic that a business person can read, an auditor can follow, and a developer can change without fear — the calculation behind an IFRS 17 analysis of change.",
        "Education")]
    [InlineData("Supercalifragilisticexpialidociousnesslikeaverylongunbrokentoken", "x", "Space")]
    public void AwkwardContent_StillRendersAValidCard(string title, string? description, string? eyebrow)
    {
        using var renderer = NewRenderer();

        var png = renderer.Render(title, description, eyebrow, title);

        var (width, height) = PngSize(png);
        Assert.Equal(1200, width);
        Assert.Equal(630, height);
    }

    // ── The picture: the node's mark, or the default badge ─────────────────────────────────

    private const string RedTile =
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 48 48'><rect width='48' height='48' rx='10' fill='#ff0000'/></svg>";

    /// <summary>The pixel at the centre of the icon square, where the mark or the badge is drawn.</summary>
    private static SKColor IconCentre(byte[] png)
    {
        using var bitmap = SKBitmap.Decode(png) ?? throw new InvalidOperationException("not a decodable PNG");
        return bitmap.GetPixel(OgCardRenderer.IconLeft + (OgCardRenderer.IconSize / 2), (int)OgCardRenderer.ContentCentreY);
    }

    private static int Luminance(SKColor c) => ((c.Red * 299) + (c.Green * 587) + (c.Blue * 114)) / 1000;

    private static OgCardContent Content(string? iconSvg = null, string? price = null, string title = "Store") =>
        new()
        {
            Title = title,
            Description = "Courses, domain plugins, agents and tools.",
            Eyebrow = "Platform",
            IconSvg = iconSvg,
            Price = price,
            Path = "Store",
            AccentSeed = "Store",
        };

    /// <summary>
    /// 🚨 THE POINT OF THE CARD. The Store shared into iMessage as a bare title beside the tiny
    /// site favicon (2026-09-18): the card had no picture on it. A node's own mark is drawn LARGE
    /// on the right — a red tile lands red pixels where the icon square is.
    /// </summary>
    [Fact]
    public void WithAnIcon_TheMarkIsDrawnLarge()
    {
        using var renderer = NewRenderer();

        var centre = IconCentre(renderer.Render(Content(iconSvg: RedTile)));

        Assert.True(centre.Red > 200 && centre.Green < 60 && centre.Blue < 60,
            $"expected the red tile at the icon square's centre, got {centre}");
    }

    /// <summary>A node with no mark still shares with a picture: the default badge — a bright
    /// accent tile carrying the page's initial — not the dark ground.</summary>
    [Fact]
    public void WithoutAnIcon_ADefaultBadgeIsDrawn()
    {
        using var renderer = NewRenderer();
        var png = renderer.Render(Content());

        var centre = IconCentre(png);
        using var bitmap = SKBitmap.Decode(png);
        var ground = bitmap.GetPixel(OgCardRenderer.IconLeft + (OgCardRenderer.IconSize / 2), OgCardRenderer.Height - 6);

        Assert.Equal(255, centre.Alpha);
        Assert.True(Luminance(centre) > 100, $"badge centre {centre} is as dark as the ground");
        Assert.True(Luminance(ground) < 60, $"the ground {ground} should be dark");
    }

    /// <summary>An authored icon that does not parse is a content defect, not a card failure:
    /// the badge is drawn instead and the card is still a valid PNG.</summary>
    [Theory]
    [InlineData("<svg><rect")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 48 48'></svg>")]
    public void AnUnusableIcon_FallsBackToTheBadge(string svg)
    {
        using var renderer = NewRenderer();

        var png = renderer.Render(Content(iconSvg: svg));

        var (width, height) = PngSize(png);
        Assert.Equal(1200, width);
        Assert.Equal(630, height);
        Assert.True(Luminance(IconCentre(png)) > 100, "no picture at all on the card");
    }

    [Fact]
    public void APrice_AndAnIcon_ChangeTheBytes()
    {
        using var renderer = NewRenderer();

        var plain = renderer.Render(Content());
        var priced = renderer.Render(Content(price: "CHF 490"));
        var marked = renderer.Render(Content(iconSvg: RedTile));

        Assert.NotEqual(plain, priced);
        Assert.NotEqual(plain, marked);
    }

    /// <summary>The instance card a non-node page shares with: a valid card that names the
    /// instance and nothing else.</summary>
    [Fact]
    public void SiteCard_IsA1200x630Png_AndDiffersByHost()
    {
        using var renderer = NewRenderer();

        var one = renderer.RenderSite("memex.meshweaver.cloud");
        var (width, height) = PngSize(one);
        Assert.Equal(1200, width);
        Assert.Equal(630, height);
        Assert.NotEqual(one, renderer.RenderSite("memex.systemorph.com"));
        Assert.NotEmpty(renderer.RenderSite(null));
    }

    /// <summary>The instance card wears the MeshWeaver mark on its navy tile, not the default
    /// badge with the site name's initial: the icon square's centre is the tile's navy, where the
    /// badge would be a bright accent, and the mark's cyan is present inside the square.</summary>
    [Fact]
    public void SiteCard_CarriesTheMeshWeaverMark_NotTheInitialBadge()
    {
        using var renderer = NewRenderer();
        var png = renderer.RenderSite("memex.meshweaver.cloud");

        using var bitmap = SKBitmap.Decode(png);
        var left = OgCardRenderer.IconLeft;
        var top = (OgCardRenderer.Height / 2) - (OgCardRenderer.IconSize / 2);
        // Just inside the tile's top-left corner: navy, not the badge's accent gradient.
        var corner = bitmap.GetPixel(left + 24, top + 24);
        Assert.True(Luminance(corner) < 40, $"the tile corner {corner} should be the mark's navy ground");
        var cyan = 0;
        for (var y = top; y < top + OgCardRenderer.IconSize; y += 2)
            for (var x = left; x < left + OgCardRenderer.IconSize; x += 2)
            {
                var px = bitmap.GetPixel(x, y);
                if (px.Blue > 200 && px.Green > 170 && px.Red < 90) cyan++;
            }
        Assert.True(cyan > 200, $"expected the cyan mark inside the icon square, found {cyan} cyan pixel(s)");
    }

    // ── The endpoint's mapping: what of the node reaches the card ──────────────────────────

    private static MeshNode Typed(string path, string nodeType, string? category, string? icon, object? content) =>
        new(path) { NodeType = nodeType, Category = category, Icon = icon, Content = content };

    /// <summary>Everything the node can say reaches the card: name, description, category, its
    /// backplated mark, a price with its currency, and the path.</summary>
    [Fact]
    public void CardContent_ReadsEverythingOffTheNode()
    {
        var node = Typed("Claims", "Store/Plugin", "Insurance", RedTile,
            JsonSerializer.SerializeToElement(new { price = 490, currency = "CHF", description = "Claims, moved to the age of agents." }));
        node = node with { Name = "Claims Deepfield" };

        var card = SeoEndpoints.CardContent(node);

        Assert.Equal("Claims Deepfield", card.Title);
        Assert.Equal("Claims, moved to the age of agents.", card.Description);
        Assert.Equal("Insurance", card.Eyebrow);
        Assert.Contains("fill='#ff0000'", card.IconSvg);
        Assert.Equal("CHF 490", card.Price);
        Assert.Equal("Claims", card.Path);
    }

    /// <summary>Without a category the eyebrow is the type's LEAF — "Plugin", not "Store/Plugin";
    /// a free page carries no price chip; a URL icon is not inline markup and yields the badge.</summary>
    [Fact]
    public void CardContent_LeafType_NoPriceWhenFree_NoSvgForAUrlIcon()
    {
        var node = Typed("Edu", "Store/Plugin", null, "/api/content/Edu/icon.png",
            JsonSerializer.SerializeToElement(new { price = 0 }));

        var card = SeoEndpoints.CardContent(node);

        Assert.Equal("Plugin", card.Eyebrow);
        Assert.Null(card.Price);
        Assert.Null(card.IconSvg);
        Assert.Equal("Edu", card.Title);
    }

    /// <summary>
    /// The accent is the thing that makes a row of shared links read as one family: stable per
    /// node (the same page always shares in the same colour) and drawn from the fixed palette, so
    /// it can never land on mud.
    /// </summary>
    [Fact]
    public void Accent_IsStablePerSeed_AndFromThePalette()
    {
        var once = OgCardRenderer.AccentFor("Claims");
        var twice = OgCardRenderer.AccentFor("Claims");
        Assert.Equal(once, twice);

        var seeds = new[] { "Claims", "Underwriting", "AgenticPrimer", "Edu", "Chess", "Pricing", "RolePlay" };
        var used = seeds.Select(OgCardRenderer.AccentFor).Distinct().Count();
        Assert.True(used > 1, "every page sharing in the same colour would defeat the point");
    }

    // ── The resolver side: which field the image comes from ────────────────────────────────

    // Top-level node: Path is Id when Namespace is empty, which is the shape every store plugin has.
    private static MeshNode Node(object content, string path = "Claims") =>
        new(path) { NodeType = "Store/Plugin", Content = content };

    private sealed record PluginLike(string? OgImage = null, string? Poster = null, string? Thumbnail = null);

    /// <summary>
    /// 🚨 THE BUG THIS FIXES. <c>PluginContent</c> declares <c>ogImage</c>; the resolver used to
    /// read only <c>poster</c>/<c>thumbnail</c>, so every plugin's hand-made og.png was ignored
    /// and no store page ever emitted an og:image at all.
    /// </summary>
    [Fact]
    public void AnAuthoredOgImage_IsUsed()
    {
        var node = Node(new PluginLike(OgImage: "/api/content/Claims/content/og.png"));

        Assert.Equal("/api/content/Claims/content/og.png", SeoResolver.ExtractImage(node));
    }

    [Fact]
    public void PosterAndThumbnail_StillWork_ForNonPluginNodes()
    {
        Assert.Equal("/api/content/X/poster.png",
            SeoResolver.ExtractImage(Node(new PluginLike(Poster: "/api/content/X/poster.png"))));
        Assert.Equal("/api/content/X/thumb.png",
            SeoResolver.ExtractImage(Node(new PluginLike(Thumbnail: "/api/content/X/thumb.png"))));
    }

    /// <summary>A page that authored nothing still shares with a card — that is the whole point.</summary>
    [Fact]
    public void WithNoAuthoredImage_ShareImageFallsBackToTheGeneratedCard()
    {
        var node = Node(new PluginLike(), "AgenticPrimer");

        Assert.Null(SeoResolver.ExtractImage(node));
        Assert.Equal("/api/og/AgenticPrimer.png", SeoResolver.ShareImage(node));
    }

    /// <summary>An authored image always wins over the generated one.</summary>
    [Fact]
    public void AnAuthoredImage_BeatsTheGeneratedCard()
    {
        var node = Node(new PluginLike(OgImage: "/api/content/Claims/content/og.png"));

        Assert.Equal("/api/content/Claims/content/og.png", SeoResolver.ShareImage(node));
    }

    /// <summary>A bare filename cannot be a share image — it would resolve against whatever path
    /// the crawler happened to fetch — so it falls through to the generated card.</summary>
    /// <summary>The head declares a size only for the card the portal DRAWS — an authored image's
    /// dimensions are unknown to it.</summary>
    [Fact]
    public void IsGeneratedCard_RecognisesTheDrawnCards_AndNotAuthoredImages()
    {
        Assert.True(SeoResolver.IsGeneratedCard("/api/og/Chess.png"));
        Assert.True(SeoResolver.IsGeneratedCard("/api/og/Edu/Courses.png"));
        Assert.True(SeoResolver.IsGeneratedCard(SeoResolver.SiteCard));
        Assert.False(SeoResolver.IsGeneratedCard("/api/content/Claims/content/og.png"));
        // An authored ABSOLUTE url is never the portal's card, whatever its path says — declaring
        // 1200×630 PNG for a banner on another host would be a lie the unfurler acts on.
        Assert.False(SeoResolver.IsGeneratedCard("https://cdn.example/api/og/banner.jpg"));
        Assert.False(SeoResolver.IsGeneratedCard("https://memex.meshweaver.cloud/api/og/Chess.png"));
        Assert.False(SeoResolver.IsGeneratedCard(null));
    }

    [Fact]
    public void ARelativeImage_IsRejected_AndFallsBack()
    {
        var node = Node(new PluginLike(OgImage: "og.png"), "Chess");

        Assert.Null(SeoResolver.ExtractImage(node));
        Assert.Equal("/api/og/Chess.png", SeoResolver.ShareImage(node));
    }
}
