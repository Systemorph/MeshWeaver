using System.Text.Json;
using Memex.Portal.Shared.Seo;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Pins <see cref="SeoPageData.Body"/> — the page text a crawler reads from the FIRST HTTP response.
/// Measured 2026-09-11 on memex.meshweaver.cloud (#4056): every public page shipped a rich head
/// and an empty body, because the only body source was the node's mirrored
/// <see cref="MeshNode.PreRenderedHtml"/>, which a plugin cover never carries and a cross-partition
/// read drops. The body must therefore come from the CONTENT, in every shape content arrives in.
/// </summary>
public class SeoPageBodyTest
{
    private static MeshNode Node(object? content) =>
        new("Page", "Space") { Name = "Page", Content = content };

    private static JsonElement Json(object value) =>
        JsonSerializer.SerializeToElement(value);

    [Fact]
    public void ANodeThatCarriesTheMirror_ServesItUnchanged()
    {
        var node = Node(new MarkdownContent { Content = "# Ignored" }) with { PreRenderedHtml = "<p>mirrored</p>" };
        Assert.Equal("<p>mirrored</p>", new SeoPageData(node, null, null).Body);
    }

    [Fact]
    public void TypedMarkdown_WithoutTheMirror_ServesItsOwnPrerenderedHtml()
    {
        // The exact shape the partitioned Postgres cross-schema read returns for a documentation
        // page: typed content that carries prerenderedHtml, and a node-level mirror that is null.
        var node = Node(new MarkdownContent { Content = "# Title", PrerenderedHtml = "<h1>Title</h1>" });
        Assert.Null(node.PreRenderedHtml);
        Assert.Equal("<h1>Title</h1>", new SeoPageData(node, null, null).Body);
    }

    [Fact]
    public void TypedMarkdown_WithNoPrerender_IsRenderedNow()
    {
        var body = new SeoPageData(Node(new MarkdownContent { Content = "Hello **world**" }), null, null).Body;
        Assert.NotNull(body);
        Assert.Contains("<strong>world</strong>", body);
    }

    [Fact]
    public void UntypedJson_PrerenderedHtmlMember_WinsOverMarkdown()
    {
        var node = Node(Json(new { content = "# raw", prerenderedHtml = "<h1>ready</h1>" }));
        Assert.Equal("<h1>ready</h1>", new SeoPageData(node, null, null).Body);
    }

    [Fact]
    public void APluginCover_RendersItsBodyMarkdown()
    {
        // A store plugin's cover (a course, the reinsurance suite) is `PluginContent { body }` —
        // markdown, never prerendered. This is the page a search for "agentic underwriting" must
        // be able to read.
        var node = Node(Json(new
        {
            body = "## Agentic underwriting\n\nTreaties, acceptances and sections as first-class nodes.",
            price = 900,
        }));
        var body = new SeoPageData(node, null, null).Body;
        Assert.NotNull(body);
        Assert.Contains("Agentic underwriting</h2>", body);
        Assert.Contains("<p>Treaties, acceptances and sections as first-class nodes.</p>", body);
    }

    [Fact]
    public void ADataNode_HasNoBody()
    {
        Assert.Null(new SeoPageData(Node(Json(new { amount = 12, currency = "CHF" })), null, null).Body);
        Assert.Null(new SeoPageData(Node(null), null, null).Body);
    }

    [Fact]
    public void RenderBody_IsPure_AndRewritesLinksAgainstTheNodePath()
    {
        // Rendering goes through the portal's own markdown pipeline, anchored on the node — the
        // same HTML a signed-in visitor sees, not a second renderer that drifts from it.
        var html = SeoResolver.RenderBody(Node(Json(new { content = "See [next](Next)." })));
        Assert.NotNull(html);
        Assert.Contains("<a href=", html);
        Assert.Contains("next</a>", html);
    }
}
