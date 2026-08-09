using System;
using System.IO;
using System.Linq;
using Memex.Portal.Shared.Seo;
using MeshWeaver.Mesh;
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
        Assert.Equal("/api/og/AgenticPrimer", SeoResolver.ShareImage(node));
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
    [Fact]
    public void ARelativeImage_IsRejected_AndFallsBack()
    {
        var node = Node(new PluginLike(OgImage: "og.png"), "Chess");

        Assert.Null(SeoResolver.ExtractImage(node));
        Assert.Equal("/api/og/Chess", SeoResolver.ShareImage(node));
    }
}
