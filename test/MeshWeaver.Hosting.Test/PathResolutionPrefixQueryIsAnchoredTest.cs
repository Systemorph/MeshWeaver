using System.Linq;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Systemorph/MeshWeaver#7754 — the path resolver's prefix-set query must stay ANCHORED whatever
/// characters the path carries.
///
/// <para><b>The production shape.</b> A request for <c>/"_content/MeshWeaver.Blazor.Portal/Layout/PortalLayoutBase.razor.js"</c>
/// (a string literal lifted out of page source, quotes and all) reached the catch-all page, and the
/// resolver wrapped every prefix in a hand-written pair of double quotes. The logged query was
/// <c>path:""_content/…razor.js""|…|""_content"</c>: it parses to no path, so the Postgres planner
/// could not pin it and served it as a UNION over every partition schema.</para>
///
/// <para><b>Negative control:</b> the old hand-quoted construction, spelled out here, parses
/// UNANCHORED — so the assertion on the new builder is what the fix changed, not a parser that
/// would have accepted either.</para>
/// </summary>
public class PathResolutionPrefixQueryIsAnchoredTest
{
    private static readonly QueryParser Parser = new();

    private static string[] Segments(string path) => path.Split('/');

    private static string[] Prefixes(string path)
    {
        var segments = Segments(path);
        return Enumerable.Range(1, segments.Length)
            .Select(depth => string.Join("/", segments.Take(depth)))
            .Reverse()
            .ToArray();
    }

    /// <summary>The query as the resolver built it before the fix.</summary>
    private static string HandQuoted(string path) =>
        "path:" + string.Join("|", Prefixes(path).Select(p => "\"" + p + "\""));

    [Theory]
    [InlineData("\"_content/MeshWeaver.Blazor.Portal/Layout/PortalLayoutBase.razor.js\"")]
    [InlineData("acme/Report \"Q3\"/Summary")]
    [InlineData("acme/Data Analytics/Deal")]
    [InlineData("acme/O'Brien/Notes")]
    [InlineData("acme")]
    public void ThePrefixQuery_ParsesToExactlyEveryPrefix_AndIsAnchored(string path)
    {
        var query = PathResolutionService.PrefixQuery(Segments(path));

        query.Should().NotBeNull();
        var parsed = Parser.Parse(query!);
        parsed.IsSufficientlySpecified().Should().BeTrue(
            $"'{query}' must name its partition, or the planner unions every partition schema");
        var expected = Prefixes(path);
        if (expected.Length == 1)
            parsed.Path.Should().Be(expected[0]);
        else
            parsed.Paths.Should().Equal(expected, "every prefix, deepest first, read back as itself");
        parsed.Filter.Should().BeNull();
        parsed.TextSearch.Should().BeNullOrEmpty("no part of a path may fall through to free text");
    }

    [Fact]
    public void NegativeControl_TheHandQuotedQuery_OfTheLoggedPath_WasUnanchored()
    {
        const string logged = "\"_content/MeshWeaver.Blazor.Portal/Layout/PortalLayoutBase.razor.js\"";
        var query = HandQuoted(logged);

        query.Should().StartWith("path:\"\"_content/", "this is the shape the incident logged");
        Parser.Parse(query).IsSufficientlySpecified().Should().BeFalse(
            "the hand-quoted prefix set of a quote-bearing path named no partition — the shape #7754 reported");
    }

    [Fact]
    public void APrefixWithBothQuoteCharacters_IsLeftOut_AndTheRestStaysAnchored()
    {
        // `acme` and `acme/a"b` are writable; `acme/a"b/c'd` holds both quote characters and has no
        // literal in a query language without escapes.
        var query = PathResolutionService.PrefixQuery(Segments("acme/a\"b/c'd"));

        query.Should().NotBeNull();
        var parsed = Parser.Parse(query!);
        parsed.IsSufficientlySpecified().Should().BeTrue();
        parsed.Paths.Should().Equal("acme/a\"b", "acme");
    }

    [Fact]
    public void APathWhoseEveryPrefixHasBothQuoteCharacters_IssuesNoQuery()
        => PathResolutionService.PrefixQuery(Segments("a'\"b/c")).Should().BeNull(
            "nothing can be written, so the resolver answers nothing instead of sending an unanchored query");
}
