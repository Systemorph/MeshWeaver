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
    [InlineData("Reinsurance/Acceptance/Source/AcceptanceReport")]
    [InlineData("with~tilde")]
    [InlineData("100%/literal%9Z-percent")]
    [InlineData("with space/and ümlaut")]
    [InlineData("Esl/EslOffice/Model/DirCarrier")]
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
        Assert.Equal("Chess/GambitHunt/Code/Chess%2FGambitHunt%2FSource%2FGambitHunt", href);
    }

    /// <summary>
    /// 🚨 Every encoded id is a VALID URL path segment: no <c>%</c> that is not followed by two hex
    /// digits. The previous <c>%9Z</c>/<c>%9Y</c> were not, and a page load of such a URL — reload,
    /// new tab, shared link — was answered HTTP 400 by the server (the NodeType side menu's
    /// Sources/Tests links on a client instance, 2026-11-03). RED on the old Encode.
    /// </summary>
    [Theory]
    [InlineData("Reinsurance/Acceptance/Source/AcceptanceReport")]
    [InlineData("path/with.dot/and/slashes.cs")]
    [InlineData("100%/x")]
    [InlineData("with space/and ümlaut")]
    public void Encode_ProducesAValidUrlSegment(string id)
    {
        var encoded = (string)WorkspaceReference.Encode(id);
        for (var i = 0; i < encoded.Length; i++)
            if (encoded[i] == '%')
                Assert.True(i + 2 < encoded.Length && Uri.IsHexDigit(encoded[i + 1]) && Uri.IsHexDigit(encoded[i + 2]),
                    $"'{encoded}' carries an invalid percent-escape at {i}");
        Assert.True(Uri.TryCreate("https://portal.example.com/Node/Code/" + encoded, UriKind.Absolute, out _));
    }

    /// <summary>Links already out there carry the legacy escapes — they still decode.</summary>
    [Fact]
    public void Decode_ReadsTheLegacyEscapes()
        => Assert.Equal("Chess/GambitHunt/Source/Gambit.Hunt",
            (string)WorkspaceReference.Decode("Chess%9ZGambitHunt%9ZSource%9ZGambit%9YHunt"));

    /// <summary>An escape a hop on the way added is decoded too — the markdown renderer writes <c>~</c>
    /// as <c>%7E</c>, a browser a space as <c>%20</c>; the portal hands the area the raw URI.</summary>
    [Fact]
    public void Decode_ReadsEscapesAddedOnTheWay()
        => Assert.Equal("Reinsurance~Acceptance with space", (string)WorkspaceReference.Decode("Reinsurance%7EAcceptance%20with%20space"));

    /// <summary>The query part rides as written — its values are unescaped by the reference's own
    /// parameter parsing, so decoding them here would let an escaped <c>&amp;</c> split a value.</summary>
    [Fact]
    public void TheQueryPart_KeepsItsEscapes()
    {
        var encoded = (string)WorkspaceReference.Encode("a/b?q=x%26y");
        Assert.Equal("a%2Fb?q=x%26y", encoded);
        Assert.Equal("a/b?q=x%26y", (string)WorkspaceReference.Decode(encoded));
    }
}
