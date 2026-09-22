using System.Text.Json;
using Memex.Portal.Shared.Seo;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Legacy Spaces may store their object as a string. Prerender must recover the same authored
/// source as the Space view, while a Markdown page remains free to contain literal JSON.
/// </summary>
public class LegacySpacePrerenderTest
{
    [Theory]
    [InlineData("body", "Current **page**", "<p>Current <strong>page</strong></p>\n")]
    [InlineData("content", "Current **page**", "<p>Current <strong>page</strong></p>\n")]
    [InlineData("body", "", "")]
    [InlineData("content", "", "")]
    [InlineData("body", " \n\t", "")]
    [InlineData("content", " \n\t", "")]
    public void SerializedSpaceSource_OverridesStaleCaches(string field, string source, string expected)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            [field] = source,
            ["prerenderedHtml"] = "<p>Stale content cache</p>",
        });
        foreach (var content in new object[] { json, JsonSerializer.SerializeToElement(json) })
        {
            var node = Space(content);
            Assert.Equal(expected, MarkdownBody.Render(node));
            Assert.Equal(expected, new SeoPageData(node, null, null).Body);
        }
    }

    [Theory]
    [InlineData("Current **body**", "<p>Current <strong>body</strong></p>\n")]
    [InlineData("", "")]
    [InlineData(" \n\t", "")]
    public void SpaceBody_WinsOverContent_InEveryObjectShape(string body, string expected)
    {
        var json = JsonSerializer.Serialize(new { body, content = "Other markdown", prerenderedHtml = "Stale" });
        foreach (var content in new object[] { json, JsonSerializer.SerializeToElement(json), JsonSerializer.Deserialize<JsonElement>(json) })
        {
            var node = Space(content);
            Assert.Equal(expected, MarkdownBody.Render(node));
            Assert.Equal(expected, new SeoPageData(node, null, null).Body);
        }
    }

    [Fact]
    public void SerializedSpaceWithoutSource_UsesItsHtmlFallback()
    {
        const string json = """{"description":"Metadata only","prerenderedHtml":"<p>Content cache</p>"}""";
        foreach (var content in new object[] { json, JsonSerializer.SerializeToElement(json) })
        {
            var node = Space(content);
            Assert.Equal("<p>Stale mirror</p>", MarkdownBody.Render(node));
            Assert.Equal("<p>Content cache</p>", MarkdownBody.Render(node with { PreRenderedHtml = null }));
        }
    }

    [Fact]
    public void SerializedSpaceWithNullBody_UsesMarkdownContent()
    {
        var node = Space("""{"body":null,"content":"Current markdown"}""") with { NodeType = "space" };
        Assert.Equal("<p>Current markdown</p>\n", MarkdownBody.Render(node));
        Assert.Equal("<p>Current markdown</p>\n", new SeoPageData(node, null, null).Body);
    }

    [Theory]
    [InlineData("Space", "{ This is **authored prose**, not JSON }", "<p>{ This is <strong>authored prose</strong>, not JSON }</p>\n")]
    [InlineData("Markdown", "{\"body\":\"Literal **JSON**\"}", "<p>{&quot;body&quot;:&quot;Literal <strong>JSON</strong>&quot;}</p>\n")]
    public void LiteralText_IsNotReinterpreted(string nodeType, string text, string expected)
    {
        foreach (var content in new object[] { text, JsonSerializer.SerializeToElement(text) })
        {
            var node = Space(content) with { NodeType = nodeType };
            Assert.Equal(expected, MarkdownBody.Render(node));
            Assert.Equal(expected, new SeoPageData(node, null, null).Body);
        }
    }

    [Fact]
    public void MarkdownObject_StillPrefersContentOverBody()
    {
        var node = Space(JsonSerializer.SerializeToElement(new { body = "Other body", content = "Markdown source" }))
            with { NodeType = "Markdown" };
        Assert.Equal("<p>Markdown source</p>\n", MarkdownBody.Render(node));
    }

    private static MeshNode Space(object content) => new("Legacy", "Site")
    {
        NodeType = "Space",
        Content = content,
        PreRenderedHtml = "<p>Stale mirror</p>",
    };
}
