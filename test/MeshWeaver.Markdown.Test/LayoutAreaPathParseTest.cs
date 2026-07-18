#pragma warning disable CS1591

using MeshWeaver.Markdown;
using Xunit;

namespace MeshWeaver.Markdown.Test;

/// <summary>
/// Pins <see cref="LayoutAreaMarkdownParser.ParseAreaAndId"/> — the keyword-aware parse of the
/// remainder an <c>IPathResolver</c> leaves after the matched node address. The pre-fix
/// <c>ParseRemainder</c> (in both <c>PathBasedLayoutArea</c> and <c>NavigationService</c>) split on
/// the first <c>/</c> and so mistook the reserved keyword for the area: <c>"area/Search"</c> →
/// area <c>"area"</c>, <c>"data/X"</c> → area <c>"data"</c>. That made EVERY keyword-form
/// <c>@@</c>-embed (and <c>/node/area/Name</c> navigation) render a non-existent area — the
/// "all these areas don't work" report. Only the bare <c>@@Search</c> form happened to work
/// (remainder <c>"Search"</c> → area <c>"Search"</c>). These cases lock the fix in.
/// </summary>
public class LayoutAreaPathParseTest
{
    [Theory]
    // Empty / null → nothing.
    [InlineData(null, null, null)]
    [InlineData("", null, null)]
    // Bare area name (the form that already worked, must keep working).
    [InlineData("Search", "Search", null)]
    [InlineData("Overview", "Overview", null)]
    // 🐛→✅ The verb "area" must be consumed, NOT taken as the area name.
    [InlineData("area/Search", "Search", null)]
    [InlineData("area/Dashboard/id1", "Dashboard", "id1")]
    [InlineData("area", null, null)]                       // bare verb → default area
    // 🐛→✅ issue #502: the COLON keyword form must resolve IDENTICALLY to the slash form. The Space
    // WelcomeMarkdown template shipped `@@("area:Search")`; the mount re-resolution (PathBasedLayoutArea)
    // ran the resolver remainder "area:Search" through here — and the pre-fix code, which split only on
    // '/', took the WHOLE "area:Search" as the area name → a non-existent area that renders BLANK (the
    // "Contents catalog no longer renders" regression). The colon form now normalises to the slash form.
    [InlineData("area:Search", "Search", null)]
    [InlineData("area:Dashboard/id1", "Dashboard", "id1")]
    // 🐛→✅ data/schema/model map to the canonical framework $-areas.
    [InlineData("data/Organization", "$Data", "Organization")]
    [InlineData("data/Type/id1", "$Data", "Type/id1")]
    [InlineData("schema/MyType", "$Schema", "MyType")]
    [InlineData("model", "$Model", null)]
    // The colon keyword form maps to the same $-areas as the slash form.
    [InlineData("data:Organization", "$Data", "Organization")]
    [InlineData("data:Type/id1", "$Data", "Type/id1")]
    [InlineData("schema:MyType", "$Schema", "MyType")]
    // "content" is NOT a hardcoded area — content collections are configured and resolved by
    // name downstream, so the first segment is kept as written (not translated to "$Content").
    [InlineData("content/logo.svg", "content", "logo.svg")]
    [InlineData("content:logo.svg", "content", "logo.svg")]
    public void ParseAreaAndId_MapsKeywordsAndBareNames(string? remainder, string? expectedArea, string? expectedId)
    {
        var (area, id) = LayoutAreaMarkdownParser.ParseAreaAndId(remainder);
        area.Should().Be(expectedArea);
        id.Should().Be(expectedId);
    }

    [Theory]
    // A trailing query string is carried into the id (areas read query params off Id).
    [InlineData("Search?q=laptop", "Search", "?q=laptop")]
    [InlineData("area/Search?q=laptop", "Search", "?q=laptop")]
    [InlineData("area/Catalog/id1?groupBy=ns", "Catalog", "id1?groupBy=ns")]
    [InlineData("data/Type?x=1", "$Data", "Type?x=1")]
    // The colon keyword form carries the query string identically — this is the tuned catalog embed
    // `@@("area:Search?groupBy=…")` the Space template documents.
    [InlineData("area:Search?groupBy=type", "Search", "?groupBy=type")]
    public void ParseAreaAndId_CarriesQueryStringIntoId(string remainder, string expectedArea, string expectedId)
    {
        var (area, id) = LayoutAreaMarkdownParser.ParseAreaAndId(remainder);
        area.Should().Be(expectedArea);
        id.Should().Be(expectedId);
    }

    [Fact]
    public void ParseAreaAndId_AreaNameKeepsOriginalCase()
    {
        // The keyword is matched case-insensitively, but the area name it precedes keeps its case.
        var (area, id) = LayoutAreaMarkdownParser.ParseAreaAndId("AREA/Search");
        area.Should().Be("Search");
        id.Should().BeNull();
    }
}
