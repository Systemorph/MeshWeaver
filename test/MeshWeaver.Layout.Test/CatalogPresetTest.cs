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
