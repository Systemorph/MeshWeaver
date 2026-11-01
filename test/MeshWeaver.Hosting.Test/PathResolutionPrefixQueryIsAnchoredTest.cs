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

    [Theory]
    [InlineData("acme/a\"b/c'd")]
    [InlineData("acme/it's-\"x\"/Notes")]
    public void APrefixWithBothQuoteCharacters_ThatABareValueCanHold_IsKept(string path)
    {
        // `acme/a"b/c'd` has no QUOTED literal, but `QueryParser.PathLiteral` writes it bare and it
        // reads back whole (PathLiteralReadsBackAsOnePathTest). Leaving it out excluded an existing
        // node at that exact path, so resolution answered an ancestor plus a remainder.
        var query = PathResolutionService.PrefixQuery(Segments(path));

        query.Should().NotBeNull();
        var parsed = Parser.Parse(query!);
        parsed.IsSufficientlySpecified().Should().BeTrue();
        parsed.Paths.Should().Equal(Prefixes(path), "every prefix, the both-quotes one included");
        parsed.Filter.Should().BeNull();
        parsed.TextSearch.Should().BeNullOrEmpty();
    }

    [Theory]
    // Both quotes AND whitespace: no value can hold it.
    [InlineData("acme/a\"b/c d'e", new[] { "acme/a\"b", "acme" })]
    public void APrefixNoValueCanHold_IsLeftOut_AndTheRestStaysAnchored(string path, string[] expected)
    {
        var query = PathResolutionService.PrefixQuery(Segments(path));

        query.Should().NotBeNull();
        var parsed = Parser.Parse(query!);
        parsed.IsSufficientlySpecified().Should().BeTrue();
        if (expected.Length == 1)
            parsed.Path.Should().Be(expected[0]);
        else
            parsed.Paths.Should().Equal(expected);
        parsed.Filter.Should().BeNull();
        parsed.TextSearch.Should().BeNullOrEmpty();
    }

    [Theory]
    // A leading quote plus the other quote character: no quoted literal, and a bare value starting
    // with a quote reads as a quoted one.
    [InlineData("'a\"b/c")]
    // Both quotes behind a list-separator head: bare inside the prefix list, `OR'x"y` would read as
    // the keyword `OR` followed by the quoted value `x"y` — a different path.
    [InlineData("OR'x\"y/z")]
    public void APathWhoseEveryPrefixNoValueCanHold_IssuesNoQuery(string path)
        => PathResolutionService.PrefixQuery(Segments(path)).Should().BeNull(
            "nothing can be written, so the resolver answers nothing instead of sending an unanchored query");
}
