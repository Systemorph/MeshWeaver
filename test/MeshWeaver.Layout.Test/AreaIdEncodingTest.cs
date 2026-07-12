using MeshWeaver.Data;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Pins the reference-segment encoding contract: an area id must survive the
/// href → route → <see cref="WorkspaceReference.Decode"/> round-trip as ONE URL
/// segment. Slashes are the load-bearing case — a path-shaped id (a source Code
/// node path in a NodeType shell's <c>{node}/Code/{id}</c> href) rendered with
/// raw slashes injects segments like <c>Source</c>/<c>Test</c> into the URL
/// resolver's prefix probe, which the satellite-table mapping routes to the
/// <c>code</c> table for the whole probe — ancestors match nothing and navigation
/// dies with "Page not found" (the Chess/GambitHunt shell links, 2026-07-12).
/// </summary>
public class AreaIdEncodingTest
{
    [Theory]
    [InlineData("Chess/GambitHunt/Source/GambitHunt")]
    [InlineData("plain")]
    [InlineData("with.dot")]
    [InlineData("path/with.dot/and/slashes.cs")]
    public void Encode_RoundTrips(string id)
    {
        var encoded = (string)WorkspaceReference.Encode(id);
        Assert.Equal(id, (string)WorkspaceReference.Decode(encoded));
    }

    [Fact]
    public void Encode_ProducesSingleSegment()
    {
        var encoded = (string)WorkspaceReference.Encode("Chess/GambitHunt/Source/GambitHunt");
        Assert.DoesNotContain("/", encoded);
        Assert.DoesNotContain(".", encoded);
    }

    [Fact]
    public void ToHref_PathShapedId_KeepsHubAreaIdAsThreeTrailingSegments()
    {
        var href = new LayoutAreaReference("Code") { Id = "Chess/GambitHunt/Source/GambitHunt" }
            .ToHref("Chess/GambitHunt");
        // hub (2 segments) + area (1) + id (1 encoded segment) — nothing the
        // path resolver could mistake for a Source/Test satellite segment.
        Assert.Equal("Chess/GambitHunt/Code/Chess%9ZGambitHunt%9ZSource%9ZGambitHunt", href);
    }
}
