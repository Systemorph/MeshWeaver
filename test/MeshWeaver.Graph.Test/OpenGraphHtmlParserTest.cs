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
        Assert.Equal(Url, preview.Url);
    }
}
