// <meshweaver>
// Id: Testing/MemexPortalShared/SeoResolverContentTest
// DisplayName: Testing/MemexPortalShared/SeoResolverContentTest — migrated from xunit (convert-xunit-to-inmesh.py)
// </meshweaver>
#nullable enable
using MeshWeaver.Reactive.Assertions;
using MeshWeaver.Testing.InMesh;
using System;
using System.Text.Json;
using Memex.Portal.Shared.Seo;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;

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

    [MeshFact]
    public void ExtractImage_UntypedJson_ReadsPosterThenThumbnail()
    {
        Assert.Equal("/api/content/S/p.png", SeoResolver.ExtractImage(Node(Json(new { poster = "/api/content/S/p.png" }))));
        Assert.Equal("/api/content/S/t.png", SeoResolver.ExtractImage(Node(Json(new { thumbnail = "/api/content/S/t.png" }))));
    }

    [MeshFact]
    public void ExtractImage_TypedMarkdownContent_ReadsThumbnail()
    {
        var content = new MarkdownContent { Content = "# page", Thumbnail = "/api/content/S/videos/x.poster.png" };

        Assert.Equal("/api/content/S/videos/x.poster.png", SeoResolver.ExtractImage(Node(content)));
    }

    [MeshFact]
    public void ExtractImage_TypedContent_ReadsPoster()
    {
        Assert.Equal("/api/content/S/og.png", SeoResolver.ExtractImage(Node(new FakePluginContent("/api/content/S/og.png", 900m))));
    }

    [MeshFact]
    public void ExtractImage_RejectsNonRootedCandidates_BothShapes()
    {
        Assert.Null(SeoResolver.ExtractImage(Node(Json(new { thumbnail = "relative.png" }))));
        Assert.Null(SeoResolver.ExtractImage(Node(new MarkdownContent { Content = "x", Thumbnail = "relative.png" })));
    }

    [MeshFact]
    public void ExtractImage_NullAndMemberlessContent_ReturnNull()
    {
        Assert.Null(SeoResolver.ExtractImage(Node(null)));
        Assert.Null(SeoResolver.ExtractImage(Node(Json(new { other = 1 }))));
        Assert.Null(SeoResolver.ExtractImage(Node(new MarkdownContent { Content = "x" })));
    }

    [MeshFact]
    public void ExtractDescription_TypedMarkdownContent_FallsBackToAbstract()
    {
        var content = new MarkdownContent { Content = "# page", Abstract = "The one-line summary." };

        Assert.Equal("The one-line summary.", SeoResolver.ExtractDescription(Node(content)));
        // The node's own Description column still wins over the content member.
        Assert.Equal("Node desc", SeoResolver.ExtractDescription(Node(content, description: "Node desc")));
    }

    [MeshFact]
    public void ContentDecimal_BothShapes_ReadPrice()
    {
        Assert.Equal(900m, SeoResolver.ContentDecimal(Node(Json(new { price = 900 })), "price"));
        Assert.Equal(900m, SeoResolver.ContentDecimal(Node(new FakePluginContent(null, 900m)), "price"));
        Assert.Null(SeoResolver.ContentDecimal(Node(new FakePluginContent(null, null)), "price"));
    }
}
