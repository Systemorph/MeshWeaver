using System.Text.Json;
using System.Text.Json.Nodes;
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
    public void ANodeWithoutSource_StillServesItsMirror()
    {
        var node = Node(null) with { PreRenderedHtml = "<p>mirrored</p>" };
        Assert.Equal("<p>mirrored</p>", new SeoPageData(node, null, null).Body);
    }

    [Fact]
    public void AnEmptyMirror_WithoutSource_FallsThroughToTheContentCache()
    {
        var node = Node(Json(new { prerenderedHtml = "<h1>Title</h1>" })) with { PreRenderedHtml = "" };
        Assert.Equal("<h1>Title</h1>", new SeoPageData(node, null, null).Body);
    }

    [Theory]
    [InlineData("markdown")]
    [InlineData("json-content")]
    [InlineData("body")]
    [InlineData("json-body")]
    [InlineData("string")]
    [InlineData("json-string")]
    [InlineData("json-object")]
    [InlineData("json-value")]
    public void AuthoredSource_WinsOverBothStaleHtmlCaches(string shape)
    {
        const string source = "Current **page**";
        const string stale = "<p>Outdated cache</p>";
        var content = SourceContent(shape, source, stale);
        var node = Node(content) with { PreRenderedHtml = stale };
        var html = new SeoPageData(node, null, null).Body;

        Assert.Contains("Current <strong>page</strong>", html);
        Assert.DoesNotContain("Outdated", html);
        // The content cache cannot win when the transient node-level mirror is absent either.
        Assert.Equal(html, new SeoPageData(node with { PreRenderedHtml = null }, null, null).Body);
    }

    private sealed record Cover(string Body, string? PrerenderedHtml);

    private static object SourceContent(string shape, string source, string stale) => shape switch
    {
        "markdown" => new MarkdownContent { Content = source, PrerenderedHtml = stale },
        "json-content" => Json(new { content = source, prerenderedHtml = stale }),
        "body" => new Cover(source, stale),
        "json-body" => Json(new { body = source, prerenderedHtml = stale }),
        "string" => source,
        "json-string" => Json(source),
        "json-object" => new JsonObject { ["body"] = source, ["prerenderedHtml"] = stale },
        "json-value" => JsonValue.Create(source)!,
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    [Fact]
    public void TypedMarkdown_WithNoPrerender_IsRenderedNow()
    {
        var body = new SeoPageData(Node(new MarkdownContent { Content = "Hello **world**" }), null, null).Body;
        Assert.NotNull(body);
        Assert.Contains("<strong>world</strong>", body);
    }

    [Theory]
    [InlineData("markdown", "")]
    [InlineData("json-content", "")]
    [InlineData("body", "")]
    [InlineData("json-body", "")]
    [InlineData("string", "")]
    [InlineData("json-string", "")]
    [InlineData("json-object", "")]
    [InlineData("json-value", "")]
    [InlineData("markdown", " \n\t")]
    [InlineData("json-body", " \n\t")]
    public void ClearingTheSource_DoesNotResurrectCachedContent(string shape, string source)
    {
        const string stale = "<h1>Deleted content</h1>";
        var node = Node(SourceContent(shape, source, stale)) with { PreRenderedHtml = stale };
        Assert.True(string.IsNullOrWhiteSpace(new SeoPageData(node, null, null).Body));
    }

    [Fact]
    public void EditingTheSource_UsesTheInteractiveRenderer_WithoutRefreshingCachedHtml()
    {
        var original = MarkdownContent.Parse("Original page", "Space/Page", "Space/Page");
        var node = Node(original) with { PreRenderedHtml = original.PrerenderedHtml };
        Assert.Contains("Original page", new SeoPageData(node, null, null).Body);

        const string changed = "## Current page\n\nSee [next](Next).\n\n<div class=\"hero\">Authored layout</div>";
        node = node with { Content = original with { Content = changed } };
        var html = new SeoPageData(node, null, null).Body;
        Assert.Equal(MarkdownViewLogic.Render(changed, node.Path, node.Path).Html, html);
        Assert.Contains("Current page", html);
        Assert.Contains("class=\"hero\"", html);
        Assert.DoesNotContain("Original page", html);
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
