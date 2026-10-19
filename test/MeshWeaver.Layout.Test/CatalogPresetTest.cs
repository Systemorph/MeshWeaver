using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// <see cref="Controls.Catalog"/> is the one way to show a set of nodes, and it must stay nothing
/// but <see cref="Controls.MeshSearch"/> with the set as its hidden query. The rules it carries were
/// each learned in a module that hand-configured its own copy: the default sort IS the hidden query
/// (so the dropdown and the list start in agreement), every option keeps the same filter (re-sorting
/// never changes WHICH nodes show), and the store orders — a client-side <c>SortBy</c> would re-sort
/// the hits and silently override the reader's pick.
/// </summary>
public class CatalogPresetTest
{
    private const string Filter = "nodeType:Crm/Counterparty scope:subtree partitions:all";

    [Fact]
    public void TheDefaultSort_IsTheHiddenQuery_AndEveryOptionKeepsTheFilter()
    {
        var catalog = Controls.Catalog("Clients", Filter, "Card");

        Assert.Equal($"{Filter} sort:subtreeLastModified-desc", catalog.HiddenQuery);
        Assert.Equal(CatalogSort.Default.Select(s => s.Label), catalog.SortOptions!.Select(o => o.Label));
        Assert.Equal(catalog.HiddenQuery, catalog.SortOptions![0].Query);
        Assert.All(catalog.SortOptions!, o => Assert.StartsWith(Filter + " sort:", o.Query));
        Assert.Null(catalog.Sorting);
    }

    [Fact]
    public void ACatalog_IsSearchable_OneHitPerRow_AndPagesAtItsPageSize()
    {
        var catalog = Controls.Catalog("Clients", Filter, "Card", pageSize: 7);

        Assert.Equal("Clients", catalog.Title);
        Assert.Equal("Card", catalog.ItemArea);
        Assert.Equal(true, catalog.ShowSearchBox);
        Assert.Equal(true, catalog.ShowViewOptions);
        Assert.Equal(MeshSearchRenderMode.Flat, catalog.RenderMode);
        Assert.Equal(1, catalog.MaxColumns);
        Assert.Equal(7, catalog.Sections?.ItemLimit);
        Assert.Equal(20, Controls.Catalog("Clients", Filter).Sections?.ItemLimit);
    }

    [Fact]
    public void OwnSorts_ReplaceTheDefaults_InTheirOrder()
    {
        var catalog = Controls.Catalog("Pipeline", Filter, sorts: [new("Next step due", "sort:content.nextStepDate"), CatalogSort.Name]);

        Assert.Equal($"{Filter} sort:content.nextStepDate", catalog.HiddenQuery);
        Assert.Equal(["Next step due", "Name"], catalog.SortOptions!.Select(o => o.Label));
    }
}
