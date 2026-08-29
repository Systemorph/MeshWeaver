
using Xunit;

namespace MeshWeaver.Markdown.Test;

/// <summary>
/// Markdown images and the content route. A collection-relative image goes through the
/// access-controlled <c>api/content/{collection}/{path}</c> route (issue #587); an image that is
/// ALREADY an address — an external <c>https://</c> picture, a <c>data:</c> URI, a site-rooted
/// <c>/api/og/…</c> — is left alone. The rewrite used to prefix everything, so a LinkedIn profile
/// photo became <c>api/content/Profiles/…/https://media.licdn.com/…</c> and an OG card image
/// <c>api/content/…//api/og/…</c>, both 404 (memex, 2026-08-29).
/// </summary>
public class MarkdownImageHrefTest
{
    [Theory]
    [InlineData("photo.png", "api/content/Profiles/Roland/photo.png")]
    [InlineData("images/hero.jpg", "api/content/Profiles/Roland/images/hero.jpg")]
    [InlineData("https://media.licdn.com/dms/image/v2/abc/profile.jpg", "https://media.licdn.com/dms/image/v2/abc/profile.jpg")]
    [InlineData("http://example.org/a.png", "http://example.org/a.png")]
    [InlineData("//cdn.example.org/a.png", "//cdn.example.org/a.png")]
    [InlineData("/api/og/OgCard/Doc", "/api/og/OgCard/Doc")]
    [InlineData("/static/NodeTypeIcons/book.svg", "/static/NodeTypeIcons/book.svg")]
    [InlineData("data:image/png;base64,iVBORw0KGgo=", "data:image/png;base64,iVBORw0KGgo=")]
    public void ResolveImageHref_RebasesOnlyCollectionRelativePaths(string url, string expected)
        => Assert.Equal(expected, MarkdownExtensions.ResolveImageHref(url, "Profiles/Roland"));

    [Fact]
    public void RenderedMarkdown_KeepsAbsoluteImages_AndRebasesRelativeOnes()
    {
        var html = Markdig.Markdown.ToHtml(
            "![me](https://media.licdn.com/dms/image/v2/abc/profile.jpg) ![local](photo.png) ![og](/api/og/OgCard/Doc)",
            MarkdownExtensions.CreateMarkdownPipeline("Profiles/Roland", "Profiles/Roland"));
        Assert.Contains("src=\"https://media.licdn.com/dms/image/v2/abc/profile.jpg\"", html);
        Assert.Contains("src=\"api/content/Profiles/Roland/photo.png\"", html);
        Assert.Contains("src=\"/api/og/OgCard/Doc\"", html);
        Assert.DoesNotContain("api/content/Profiles/Roland/https://", html);
        Assert.DoesNotContain("api/content/Profiles/Roland//api", html);
    }
}
