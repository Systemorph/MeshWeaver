#pragma warning disable CS1591

using System.Text.Json;
using MeshWeaver.Graph;
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
        var space = new Space { Name = "Acme", Body = "BODY" };
        Assert.Same(space, SpaceLayoutAreas.ResolveSpace(Node(space), Options));
    }

    [Fact]
    public void JsonElementContent_Deserialized()
    {
        var je = JsonSerializer.SerializeToElement(new Space { Name = "Acme", Body = "BODY" }, Options);
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
        var space = SpaceLayoutAreas.ResolveSpace(Node("# Hello\n\nplain text body"), Options);
        space.Should().NotBeNull();
        space!.Body.Should().Contain("plain text body");
        space.Name.Should().Be("Acme");
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
}
