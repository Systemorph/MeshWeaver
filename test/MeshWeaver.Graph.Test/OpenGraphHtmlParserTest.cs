using MeshWeaver.Graph;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins <see cref="OpenGraphHtmlParser"/> across the head shapes real portals serve: the
/// <c>og:*</c> tags a MeshWeaver portal's SEO head emits (attribute order varies by renderer),
/// the standard fallbacks (<c>&lt;title&gt;</c>, <c>&lt;meta name="description"&gt;</c>),
/// HTML-entity decoding, and relative-image resolution.
/// </summary>
public class OpenGraphHtmlParserTest
{
    private const string Url = "https://portal.example.org/Underwriting";

    [Fact]
    public void Parse_PortalShapedHead_ReadsAllOgTags()
    {
        // The exact tag shape memex portals serve (SeoHead), including the &#x2014; entity.
        const string html = """
            <html><head>
            <meta property="og:site_name" content="Memex" />
            <meta property="og:type" content="website">
            <meta property="og:title" content="Underwriting" />
            <meta property="og:description" content="Underwriting, moved to the age of agents &#x2014; the governed workflow." />
            <meta property="og:url" content="https://portal.example.org/Underwriting" />
            <meta property="og:image" content="https://portal.example.org/api/og/Underwriting" />
            </head><body>ignored</body></html>
            """;

        var preview = OpenGraphHtmlParser.Parse(Url, html);

        Assert.Equal("Underwriting", preview.Title);
        Assert.Equal("Underwriting, moved to the age of agents — the governed workflow.",
            preview.Description);
        Assert.Equal("https://portal.example.org/api/og/Underwriting", preview.Image);
        Assert.Equal("Memex", preview.SiteName);
        Assert.True(preview.Fetched);
    }

    [Fact]
    public void Parse_ContentBeforeProperty_StillMatches()
    {
        const string html =
            "<head><meta content=\"Reversed\" property=\"og:title\"></head>";

        Assert.Equal("Reversed", OpenGraphHtmlParser.Parse(Url, html).Title);
    }

    [Fact]
    public void Parse_NoOgTags_FallsBackToTitleAndMetaDescription()
    {
        const string html = """
            <head><title>Plain &amp; Simple</title>
            <meta name="description" content="A plain page."></head>
            """;

        var preview = OpenGraphHtmlParser.Parse(Url, html);

        Assert.Equal("Plain & Simple", preview.Title);
        Assert.Equal("A plain page.", preview.Description);
        Assert.Null(preview.Image);
    }

    [Fact]
    public void Parse_RelativeImage_ResolvesAgainstPageUrl()
    {
        const string html = "<head><meta property=\"og:image\" content=\"/api/og/X.png\"></head>";

        Assert.Equal("https://portal.example.org/api/og/X.png",
            OpenGraphHtmlParser.Parse(Url, html).Image);
    }

    [Fact]
    public void Parse_FirstOccurrenceWins_AndBodyTagsAreIgnored()
    {
        const string html = """
            <head><meta property="og:title" content="First"></head>
            <body><meta property="og:title" content="InBody"></body>
            """;

        Assert.Equal("First", OpenGraphHtmlParser.Parse(Url, html).Title);
    }

    [Fact]
    public void Parse_EmptyOrTagless_YieldsNullFields()
    {
        var preview = OpenGraphHtmlParser.Parse(Url, "<head></head>");

        Assert.Null(preview.Title);
        Assert.Null(preview.Description);
        Assert.Null(preview.Image);
        Assert.Null(preview.Icon);
        Assert.Equal(Url, preview.Url);
    }

    /// <summary>
    /// 🚨 The exact head a MeshWeaver (Blazor) portal serves: <c>&lt;base href="/"&gt;</c> plus a
    /// RELATIVE <c>href="favicon.ico"</c>. Resolved against the PAGE URL this would yield
    /// <c>/PartnerRe/favicon.ico</c> — which a catch-all-routed SPA answers with 200 text/html,
    /// not a 404, i.e. a silently broken image. The base tag is what makes it resolve to the real
    /// origin-root icon.
    /// </summary>
    [Fact]
    public void Parse_RelativeIcon_ResolvesAgainstBaseHref_NotThePagePath()
    {
        const string nested = "https://portal.example.org/PartnerRe/EslProposalQA";
        const string html = """
            <html><head><base href="/">
            <link rel="icon" type="image/png" href="favicon.ico">
            </head><body></body></html>
            """;

        Assert.Equal("https://portal.example.org/favicon.ico",
            OpenGraphHtmlParser.Parse(nested, html).Icon);
    }

    [Fact]
    public void Parse_RelativeIcon_NoBaseTag_ResolvesAgainstPageUrl()
    {
        const string html = "<head><link rel=\"shortcut icon\" href=\"/icons/site.png\"></head>";

        Assert.Equal("https://portal.example.org/icons/site.png",
            OpenGraphHtmlParser.Parse(Url, html).Icon);
    }

    /// <summary>Ranking is on ONE axis — effective pixel size — so the icon that renders sharpest
    /// in the card's 48 px box wins regardless of declaration order.</summary>
    [Fact]
    public void Parse_PrefersLargestDeclaredIcon()
    {
        const string html = """
            <head>
            <link rel="icon" sizes="16x16" href="/small.png">
            <link rel="icon" sizes="192x192" href="/large.png">
            <link rel="icon" sizes="32x32" href="/medium.png">
            </head>
            """;

        Assert.Equal("https://portal.example.org/large.png",
            OpenGraphHtmlParser.Parse(Url, html).Icon);
    }

    [Fact]
    public void Parse_ScalableSvgIcon_OutranksEveryRaster()
    {
        const string html = """
            <head>
            <link rel="apple-touch-icon" sizes="180x180" href="/touch.png">
            <link rel="icon" type="image/svg+xml" href="/site.svg">
            </head>
            """;

        Assert.Equal("https://portal.example.org/site.svg",
            OpenGraphHtmlParser.Parse(Url, html).Icon);
    }

    /// <summary>A sizeless <c>apple-touch-icon</c> is conventionally 180 px and purpose-built as a
    /// square tile, so it outranks a sizeless <c>favicon.ico</c> (conventionally 16–32 px).</summary>
    [Fact]
    public void Parse_SizelessAppleTouchIcon_OutranksSizelessFavicon()
    {
        const string html = """
            <head>
            <link rel="icon" href="/favicon.ico">
            <link rel="apple-touch-icon" href="/touch.png">
            </head>
            """;

        Assert.Equal("https://portal.example.org/touch.png",
            OpenGraphHtmlParser.Parse(Url, html).Icon);
    }

    [Fact]
    public void Parse_DataUriIcon_ReturnedVerbatim()
    {
        const string data = "data:image/svg+xml,%3Csvg%20xmlns%3D'http%3A%2F%2Fwww.w3.org%2F2000%2Fsvg'%2F%3E";
        var html = $"<head><link rel=\"icon\" href=\"{data}\"></head>";

        Assert.Equal(data, OpenGraphHtmlParser.Parse(Url, html).Icon);
    }

    /// <summary><c>mask-icon</c> is a monochrome Safari pinned-tab silhouette, not the site
    /// icon — it must never be picked, not even as the only candidate.</summary>
    [Fact]
    public void Parse_MaskIconAndStylesheet_AreNotIconCandidates()
    {
        const string html = """
            <head>
            <link rel="stylesheet" href="/app.css">
            <link rel="mask-icon" href="/pinned.svg" color="#000">
            </head>
            """;

        Assert.Null(OpenGraphHtmlParser.Parse(Url, html).Icon);
    }

    /// <summary>Attribute names are matched as WHOLE attributes: a <c>data-href</c> decoy
    /// preceding the real <c>href</c> must not be read as the icon.</summary>
    [Fact]
    public void Parse_AttributeLookalikes_DoNotHijackTheIcon()
    {
        const string html =
            "<head><link rel=\"icon\" data-href=\"/decoy.png\" href=\"/real.png\"></head>";

        Assert.Equal("https://portal.example.org/real.png",
            OpenGraphHtmlParser.Parse(Url, html).Icon);
    }

    /// <summary>The card visual is the ICON, never the poster: declared icon → og:image →
    /// conventional origin-root favicon.</summary>
    [Fact]
    public void CardIcon_PrefersDeclaredIcon_ThenOgImage_ThenConventionalFavicon()
    {
        const string withIcon = """
            <head>
            <link rel="icon" href="/site.png">
            <meta property="og:image" content="/api/og/Poster.png">
            </head>
            """;
        Assert.Equal("https://portal.example.org/site.png",
            OpenGraphHtmlParser.Parse(Url, withIcon).CardIcon);

        // No icon declared → the poster is the only visual the page declared, so it is used
        // rather than guessing at a favicon that may 404.
        const string posterOnly = "<head><meta property=\"og:image\" content=\"/api/og/P.png\"></head>";
        Assert.Equal("https://portal.example.org/api/og/P.png",
            OpenGraphHtmlParser.Parse(Url, posterOnly).CardIcon);

        // Nothing declared → the conventional origin-root favicon, resolved off the ORIGIN
        // (never the page path).
        Assert.Equal("https://portal.example.org/favicon.ico",
            OpenGraphHtmlParser.Parse(Url, "<head><title>T</title></head>").CardIcon);
    }

    /// <summary>An unreachable target's origin is unproven, so the card guesses nothing and keeps
    /// its clean initials placeholder instead of showing a broken image.</summary>
    [Fact]
    public void CardIcon_UnfetchedTarget_GuessesNothing()
    {
        Assert.Null(OpenGraphPreview.Unavailable(Url).CardIcon);
    }
}
