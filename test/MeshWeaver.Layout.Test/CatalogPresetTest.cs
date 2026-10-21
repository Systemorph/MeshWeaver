using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// <see cref="Controls.Catalog"/> is the one way to show a set of nodes, and it must stay nothing
/// but <see cref="Controls.MeshSearch"/> with the set as its hidden query: a search bar, one hit per
/// row, store-side pages with "Load more". No sort dropdown and no view-options bar — the query
/// language is typed into the same bar, the placeholder teaches it by example, and the store orders
/// (a client-side <c>SortBy</c> would re-sort the hits behind the reader's back).
/// </summary>
public class CatalogPresetTest
{
    private const string Filter = "nodeType:Crm/Counterparty scope:subtree partitions:all";

    [Fact]
    public void TheHiddenQuery_IsTheFilterWithTheDefaultOrder_LastActivity()
    {
        var catalog = Controls.Catalog("Clients", Filter, "Card");

        Assert.Equal($"{Filter} sort:subtreeLastModified-desc", catalog.HiddenQuery);
        Assert.Null(catalog.Sorting);
        Assert.Equal($"{Filter} sort:name", Controls.Catalog("Clients", Filter, sort: CatalogSort.Name).HiddenQuery);
    }

    /// <summary>
    /// A union hidden query (one sub-query per line) is ordered by its FIRST line — the view sends the
    /// lines as <c>MeshQueryRequest.FromQueries</c>, each followed by the typed text, and the engine
    /// takes sort/limit from the first. So EVERY line carries the order (a sort on the last line alone
    /// left the union unsorted); a line with its own <c>sort:</c> keeps it; and a typed <c>sort:</c>
    /// still wins on the line that orders the union.
    /// </summary>
    [Fact]
    public void AUnion_CarriesTheOrderOnEveryLine_AnOwnSortIsKept_AndATypedSortStillWins()
    {
        const string first = "nodeType:Crm/Counterparty namespace:Fabrikam";
        const string second = "nodeType:Crm/Counterparty namespace:Globex";
        const string own = "nodeType:Crm/Counterparty namespace:Acme sort:name";

        var hidden = Controls.Catalog("Clients", $"{first}\n{second}\n{own}").HiddenQuery!.ToString()!;
        var lines = hidden.Split('\n');

        Assert.Equal(3, lines.Length);
        Assert.Equal($"{first} sort:subtreeLastModified-desc", lines[0]);
        Assert.Equal($"{second} sort:subtreeLastModified-desc", lines[1]);

        // As the production parser reads each line: the default on the first two, the own order kept.
        var parser = new MeshWeaver.Mesh.QueryParser();
        Assert.Equal(["subtreeLastModified", "subtreeLastModified", "name"], lines.Select(l => parser.Parse(l).OrderBy?.Property));
        // The view's combination of the line that orders the union: the line, then the typed text.
        Assert.Equal("name", parser.Parse($"{lines[0]} sort:name").OrderBy?.Property);
        Assert.Equal("lastModified", parser.Parse($"{lines[2]} sort:lastModified-desc").OrderBy?.Property);
    }

    /// <summary>Whether a line "has its own order" is decided the way the PARSER reads it, not by a
    /// text match: <c>sort:</c> inside a quoted value is no order (the default still applies), and a
    /// grouped <c>(sort:name)</c> is one (it is kept, not overridden by the default).</summary>
    [Theory]
    [InlineData("namespace:Acme content.note:\"use sort:name\"", "subtreeLastModified")]
    [InlineData("namespace:Acme (sort:name)", "name")]
    [InlineData("namespace:Acme SORT:name", "name")]
    public void AnOwnOrder_IsWhatTheParserReads_NotATextMatch(string line, string expected)
    {
        var parser = new MeshWeaver.Mesh.QueryParser();
        Assert.Equal(expected, parser.Parse(CatalogSort.Sorted(line, CatalogSort.LastActivity.Sort)).OrderBy?.Property);
        Assert.Equal(expected, parser.Parse(CatalogSort.Sorted($"namespace:Globex\n{line}", CatalogSort.LastActivity.Sort).Split('\n')[1]).OrderBy?.Property);
    }

    [Fact]
    public void ACatalog_IsASearchBar_AndLoadMore_Only()
    {
        var catalog = Controls.Catalog("Clients", Filter, "Card", pageSize: 7);

        Assert.Equal("Clients", catalog.Title);
        Assert.Equal("Card", catalog.ItemArea);
        Assert.Equal(true, catalog.ShowSearchBox);
        Assert.Null(catalog.ShowViewOptions);
        Assert.Null(catalog.SortOptions);
        Assert.Equal(MeshSearchRenderMode.Flat, catalog.RenderMode);
        Assert.Equal(1, catalog.MaxColumns);
        Assert.Equal(7, catalog.PageSize);
        Assert.Null(catalog.Sections?.ItemLimit);
        Assert.Equal(20, Controls.Catalog("Clients", Filter).PageSize);
    }

    [Fact]
    public void ThePlaceholder_TeachesTheQueryLanguage_ByExample()
    {
        var placeholder = (string)Controls.Catalog("Partners", Filter).Placeholder!;

        Assert.StartsWith("Search partners", placeholder);
        Assert.Contains("sort:name", placeholder);
        Assert.Contains("sort:lastModified-desc", placeholder);
        Assert.DoesNotContain("subtreeLastModified", placeholder);
    }
}
