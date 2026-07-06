#pragma warning disable CS1591

using System.Text.Json;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Unit coverage for <see cref="SpaceLayoutAreas.ResolveSpace"/> — the robust reader behind
/// the Space Overview. The Space lives on <see cref="MeshNode.Content"/> (there is no Space
/// data stream), and Content can arrive in several shapes: an already-typed Space, a degraded
/// JsonElement, or — when it lost its typing — a raw string. The reader must recover all of
/// them rather than falling back to the welcome placeholder.
/// </summary>
public class SpaceResolveSpaceTest
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private static MeshNode Node(object? content) =>
        new("Acme", "") { Name = "Acme", NodeType = "Space", Content = content };

    [Fact]
    public void TypedContent_ReturnedAsIs()
    {
        var space = new Space { Body = "BODY" };
        Assert.Same(space, SpaceLayoutAreas.ResolveSpace(Node(space), Options));
    }

    [Fact]
    public void JsonElementContent_Deserialized()
    {
        var je = JsonSerializer.SerializeToElement(new Space { Body = "BODY" }, Options);
        SpaceLayoutAreas.ResolveSpace(Node(je), Options)!.Body.Should().Be("BODY");
    }

    [Fact]
    public void JsonObjectString_DeserializedIntoSpace()
    {
        // "just a string" that is actually a JSON object — recover the real Space.
        var json = """{"name":"Acme","body":"BODY"}""";
        SpaceLayoutAreas.ResolveSpace(Node(json), Options)!.Body.Should().Be("BODY");
    }

    [Fact]
    public void PlainString_TakenAsBody()
    {
        // "just a string" that is NOT JSON — keep it as the body so the page still shows it.
        // The name is no longer carried on Space.Content; it lives on MeshNode.Name (the single
        // source of truth), so assert the node name rather than a (now-removed) space.Name.
        var node = Node("# Hello\n\nplain text body");
        var space = SpaceLayoutAreas.ResolveSpace(node, Options);
        space.Should().NotBeNull();
        space!.Body.Should().Contain("plain text body");
        node.Name.Should().Be("Acme");
    }

    [Fact]
    public void JsonStringElement_TakenAsBody()
    {
        // A JsonElement whose ValueKind is String (not Object) — same recovery as a raw string.
        var je = JsonSerializer.SerializeToElement("a plain body line");
        SpaceLayoutAreas.ResolveSpace(Node(je), Options)!.Body.Should().Contain("a plain body line");
    }

    [Fact]
    public void NullContent_ReturnsNull()
    {
        SpaceLayoutAreas.ResolveSpace(Node(null), Options).Should().BeNull();
    }

    // ---------- BuildBodyContent carries the Space path as NodePath ----------
    // The body markdown is a child of the Overview area; its stream owner is not a reliable
    // node-path source, so a relative @@-embed in an authored body would render an unaddressed
    // (dead) layout area. BuildBodyContent must stamp NodePath = the Space path so MarkdownView
    // resolves the embed. (The default welcome no longer ships @@("area/Search") — the catalog is
    // a fixed section via BuildNavigation — but NodePath is still stamped for authored bodies.)

    [Fact]
    public void BuildBodyContent_DefaultWelcome_CarriesSpacePathAsNodePath()
    {
        var control = SpaceLayoutAreas.BuildBodyContent(space: null, Node(null), spacePath: "Acme");

        var md = Assert.IsType<MarkdownControl>(control);
        md.NodePath.Should().Be("Acme",
            "the welcome body must carry the Space path so authored @@-embeds resolve");
        md.Markdown.ToString()!.Should().Contain("Welcome", "the default welcome placeholder is shown");
    }

    [Fact]
    public void BuildBodyContent_AuthoredBody_CarriesSpacePathAsNodePath()
    {
        var space = new Space { Body = "# Hi\n\n@@(\"area/Search\")" };
        var control = SpaceLayoutAreas.BuildBodyContent(space, Node(space), spacePath: "Acme");

        var md = Assert.IsType<MarkdownControl>(control);
        md.NodePath.Should().Be("Acme");
        md.Markdown.ToString()!.Should().Contain("Hi");
    }
}
