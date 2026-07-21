using System.Text.Json;
using Memex.Portal.Shared.Seo;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Pins <see cref="SeoResolver"/>'s content-member reads across BOTH content shapes. A node's
/// Content reaches the SEO surface either as an untyped <see cref="JsonElement"/> (node-native
/// types the portal hub hasn't registered) or as a TYPED record when the hub knows the CLR type —
/// a markdown page resolves as <see cref="MarkdownContent"/>. Reading only the JsonElement shape
/// silently dropped <c>og:image</c> for every markdown node's <c>thumbnail</c> (UWDeepfield /
/// ClaimsDeepfield Overview share cards, 2026-07).
/// </summary>
public class SeoResolverContentTest
{
    private sealed record FakePluginContent(string? Poster, decimal? Price);

    private static MeshNode Node(object? content, string? description = null) =>
        new("Overview", "Space") { NodeType = "Markdown", Description = description, Content = content };

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void ExtractImage_UntypedJson_ReadsPosterThenThumbnail()
    {
        Assert.Equal("/static/S/p.png", SeoResolver.ExtractImage(Node(Json(new { poster = "/static/S/p.png" }))));
        Assert.Equal("/static/S/t.png", SeoResolver.ExtractImage(Node(Json(new { thumbnail = "/static/S/t.png" }))));
    }

    [Fact]
    public void ExtractImage_TypedMarkdownContent_ReadsThumbnail()
    {
        var content = new MarkdownContent { Content = "# page", Thumbnail = "/static/S/videos/x.poster.png" };

        Assert.Equal("/static/S/videos/x.poster.png", SeoResolver.ExtractImage(Node(content)));
    }

    [Fact]
    public void ExtractImage_TypedContent_ReadsPoster()
    {
        Assert.Equal("/static/S/og.png", SeoResolver.ExtractImage(Node(new FakePluginContent("/static/S/og.png", 900m))));
    }

    [Fact]
    public void ExtractImage_RejectsNonRootedCandidates_BothShapes()
    {
        Assert.Null(SeoResolver.ExtractImage(Node(Json(new { thumbnail = "relative.png" }))));
        Assert.Null(SeoResolver.ExtractImage(Node(new MarkdownContent { Content = "x", Thumbnail = "relative.png" })));
    }

    [Fact]
    public void ExtractImage_NullAndMemberlessContent_ReturnNull()
    {
        Assert.Null(SeoResolver.ExtractImage(Node(null)));
        Assert.Null(SeoResolver.ExtractImage(Node(Json(new { other = 1 }))));
        Assert.Null(SeoResolver.ExtractImage(Node(new MarkdownContent { Content = "x" })));
    }

    [Fact]
    public void ExtractDescription_TypedMarkdownContent_FallsBackToAbstract()
    {
        var content = new MarkdownContent { Content = "# page", Abstract = "The one-line summary." };

        Assert.Equal("The one-line summary.", SeoResolver.ExtractDescription(Node(content)));
        // The node's own Description column still wins over the content member.
        Assert.Equal("Node desc", SeoResolver.ExtractDescription(Node(content, description: "Node desc")));
    }

    [Fact]
    public void ContentDecimal_BothShapes_ReadPrice()
    {
        Assert.Equal(900m, SeoResolver.ContentDecimal(Node(Json(new { price = 900 })), "price"));
        Assert.Equal(900m, SeoResolver.ContentDecimal(Node(new FakePluginContent(null, 900m)), "price"));
        Assert.Null(SeoResolver.ContentDecimal(Node(new FakePluginContent(null, null)), "price"));
    }
}
