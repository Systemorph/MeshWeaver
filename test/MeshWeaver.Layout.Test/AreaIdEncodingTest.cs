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
    [InlineData("100%/literal%9Z-and%2F-percent")]
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

    /// <summary>
    /// 🚨 Review: an all-dots id ("." or "..") must not become a URL DOT SEGMENT — `%2E` / `%2E%2E` are
    /// removed by URL parsing before navigation. Asserted on the PARSED URL's path, not on whether a URL
    /// could be created, plus the round-trip; ids that merely contain dots are unaffected.
    /// </summary>
    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("...")]
    [InlineData("..x")]
    [InlineData("%3B")]
    public void AnAllDotsId_SurvivesUrlParsing_AndRoundTrips(string id)
    {
        var encoded = (string)WorkspaceReference.Encode(id);
        // The parsed URL keeps the id as its own third segment — a dot segment would be removed
        // (`/Node/Code/%2E%2E` parses to `/Node/`). (.NET's Uri unescapes %2E in AbsolutePath, so the
        // segment is compared by structure, not byte for byte.)
        var url = new Uri("https://portal.example.com/Node/Code/" + encoded);
        var segments = url.AbsolutePath.Trim('/').Split('/');
        Assert.Equal(new[] { "Node", "Code" }, segments[..2]);
        Assert.Equal(3, segments.Length);
        Assert.NotEqual(".", segments[2]);
        Assert.NotEqual("..", segments[2]);
        Assert.Equal(id, (string)WorkspaceReference.Decode(encoded));
    }

    /// <summary>Links already out there carry the legacy escapes — they still decode.</summary>
    [Fact]
    public void Decode_ReadsTheLegacyEscapes()
        => Assert.Equal("Chess/GambitHunt/Source/Gambit.Hunt",
            (string)WorkspaceReference.Decode("Chess%9ZGambitHunt%9ZSource%9ZGambit%9YHunt"));

    /// <summary>
    /// 🚨 Decode also runs over internal DataContext pointers (DispatchView), which carry JSON escaping,
    /// not URI escaping: a pointer for a bound data id containing a literal percent-escape must come back
    /// unchanged, or the binding breaks. Only Encode's own escapes and the legacy ones are decoded.
    /// </summary>
    [Theory]
    [InlineData("literal%22id%20x")]
    [InlineData("literal%2Fid")]
    [InlineData("literal%2Eid")]
    [InlineData("literal%25id")]
    [InlineData("legacy%9Zid%9Y")]
    public void Decode_LeavesADataContextPointerIntact_WhateverItsIdContains(string dataId)
    {
        var pointer = LayoutAreaReference.GetDataPointer(dataId);
        Assert.Equal(pointer, (string)WorkspaceReference.Decode(pointer));
        // …while the same text as a URL id segment is decoded (only Encode's own escapes).
        Assert.Equal("a%7Eb", (string)WorkspaceReference.Decode("a%7Eb"));
    }

    /// <summary>An escaped percent is decoded exactly once.</summary>
    [Fact]
    public void Decode_IsOnePass()
        => Assert.Equal("a%2Fb", (string)WorkspaceReference.Decode("a%252Fb"));

    [Fact]
    public void DecodeAreaId_IsTheUrlDecoder_AndDecodeDelegatesToItForIds()
    {
        Assert.Equal("Store/Plugin/Source/X", WorkspaceReference.DecodeAreaId("Store%2FPlugin%2FSource%2FX"));
        Assert.Equal(WorkspaceReference.DecodeAreaId("a%2Eb%9Zc"), (string)WorkspaceReference.Decode("a%2Eb%9Zc"));
    }

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
