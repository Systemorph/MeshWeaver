using System.Text.Json;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins <see cref="VersionLayoutArea.ExtractDiffContent"/>'s shape tolerance: the version diff must
/// show the PROSE of a node whatever shape its content arrives in. The regression this guards: a
/// dynamic type's content (e.g. a SocialMedia/Post, whose body lives in a <c>text</c> field and
/// arrives TYPED on its own hub) fell through to a whole-node JSON dump, so "compare versions" on a
/// post diffed serialized envelopes instead of the post text.
/// </summary>
public class VersionDiffContentTest
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private sealed record SocialPostLike
    {
        public string Text { get; init; } = "";
        public string Status { get; init; } = "Draft";
    }

    [Fact]
    public void MarkdownContent_ExtractsBody()
    {
        var node = new MeshNode("Doc", "Space") { Content = new MarkdownContent { Content = "# body" } };
        Assert.Equal("# body", VersionLayoutArea.ExtractDiffContent(node, Options));
        Assert.True(VersionLayoutArea.IsMarkdownContent(node, Options));
    }

    [Fact]
    public void TypedContentWithTextField_ExtractsTheText_NotAJsonDump()
    {
        // The current side of a post diff: content is a TYPED object (runtime-compiled on the
        // post's own hub) whose prose lives in `text`.
        var node = new MeshNode("Post", "Posts")
        {
            Content = new SocialPostLike { Text = "After 30 years on Windows, I bought a Mac." },
        };
        Assert.Equal("After 30 years on Windows, I bought a Mac.",
            VersionLayoutArea.ExtractDiffContent(node, Options));
        Assert.True(VersionLayoutArea.IsMarkdownContent(node, Options));
    }

    [Fact]
    public void JsonElementContentWithTextField_ExtractsTheText()
    {
        // The historical side: the version store deserializes content as a JsonElement.
        var element = JsonSerializer.SerializeToElement(
            new SocialPostLike { Text = "the historical post text" }, Options);
        var node = new MeshNode("Post", "Posts") { Content = element };
        Assert.Equal("the historical post text", VersionLayoutArea.ExtractDiffContent(node, Options));
    }

    [Fact]
    public void NonTextContent_FallsBackToContentJson_NotTheNodeEnvelope()
    {
        var element = JsonSerializer.SerializeToElement(new { price = 42, currency = "CHF" }, Options);
        var node = new MeshNode("Thing", "Space") { Content = element, Version = 17 };
        var extracted = VersionLayoutArea.ExtractDiffContent(node, Options);
        Assert.Contains("42", extracted);
        Assert.Contains("CHF", extracted);
        // The envelope must NOT leak into the diff — version/lastModified noise buries the change.
        Assert.DoesNotContain("\"version\"", extracted);
        Assert.DoesNotContain("nodeType", extracted);
        Assert.False(VersionLayoutArea.IsMarkdownContent(node, Options));
    }

    [Fact]
    public void NullContent_IsEmpty()
    {
        Assert.Equal("", VersionLayoutArea.ExtractDiffContent(new MeshNode("X", "Space"), Options));
        Assert.Equal("", VersionLayoutArea.ExtractDiffContent(null, Options));
    }
}
