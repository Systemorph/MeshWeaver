using System.Reactive.Linq;
using System.Reactive.Subjects;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// <see cref="CatalogApp"/> is a menu of categories, each a column of <see cref="Controls.Catalog"/>s,
/// with ONE query bar shared by all of them. These pin the contract adopters build on: the menu's
/// area ids and labels, the hidden queries, the typed text riding as the visible query and only ever
/// narrowing, the computed-filter empty state, and that nothing re-renders on a repeated value.
/// </summary>
public class CatalogAppTest
{
    private const string Clients = "nodeType:Crm/Counterparty scope:subtree content.type:Crm/CounterpartyType/Client partitions:all";
    private const string Deals = "nodeType:Crm/Opportunity scope:subtree isOpen:true partitions:all";

    private static readonly IObservable<string> NothingTyped = Observable.Return("");

    private static List<UiControl> Collect(IObservable<UiControl> source)
    {
        var seen = new List<UiControl>();
        source.Subscribe(seen.Add);
        return seen;
    }

    [Fact]
    public void TheMenu_HasOneTabPerCategory_IdIsTheAddress_LabelIsTheSkin()
    {
        var app = new CatalogApp("Portfolio")
            .WithCategory("clients", "Clients", CatalogSearch.Of(Clients, itemArea: "Card"))
            .WithCategory("deals", "Deals", CatalogSearch.Of(Deals, "Open"));

        var menu = CatalogApp.Menu(app.Categories, NothingTyped, selected: "deals");

        Assert.Equal(["clients", "deals"], menu.Areas.Select(a => a.Id?.ToString()));
        Assert.Equal(["Clients", "Deals"], menu.Areas.Select(a => a.Skins.OfType<TabSkin>().Single().Label));
        Assert.Equal("deals", menu.Skin.ActiveTabId);
        Assert.Null(CatalogApp.Menu(app.Categories, NothingTyped, selected: "nope").Skin.ActiveTabId);
    }

    [Fact]
    public void ASearch_IsTheCatalog_WithTheBarHidden_AndTheTypedTextAsVisibleQuery()
    {
        var section = (MeshSearchControl)CatalogSearch.Of(Clients, itemArea: "Card", sort: CatalogSort.Name)
            .Render(Clients, "fabrikam sort:lastModified-desc", showCreate: true, categoryLabel: "Clients");

        Assert.Equal($"{Clients} sort:name", section.HiddenQuery);
        Assert.Equal("fabrikam sort:lastModified-desc", section.VisibleQuery);
        Assert.Equal(false, section.ShowSearchBox);
        Assert.Equal("Clients", section.Title);
        Assert.Equal("Card", section.ItemArea);
        Assert.Equal(1, section.MaxColumns);
        Assert.Equal(20, section.PageSize);
    }

    [Fact]
    public void TheCreatePlus_And_Columns_And_TheHook_AreApplied()
    {
        var search = CatalogSearch.Of(Deals, "Open").WithCreate("Crm/Opportunity", "Fabrikam").WithMaxColumns(3)
            .WithConfigure(c => c.WithGroupBy("nodeType"));

        var shown = (MeshSearchControl)search.Render(Deals, "", showCreate: true, "Deals");
        var hidden = (MeshSearchControl)search.Render(Deals, "", showCreate: false, "Deals");

        Assert.Equal("Crm/Opportunity", shown.CreateNodeType);
        Assert.Equal("Fabrikam", shown.CreateNamespace);
        Assert.Equal(3, shown.MaxColumns);
        Assert.NotNull(shown.Grouping);
        Assert.Null(hidden.CreateNodeType);
    }

    [Fact]
    public void TypedText_CanOnlyNarrow_AnchoringTermsAreDropped()
    {
        Assert.Equal("fabrikam sort:name content.offering:X -path:Fabrikam/Old",
            CatalogQuery.Typed("fabrikam namespace:Globex sort:name partitions:all content.offering:X scope:descendants -path:Fabrikam/Old"));
        Assert.Equal("\"a namespace:b\"", CatalogQuery.Typed("\"a namespace:b\" path:X"));
        Assert.Equal("", CatalogQuery.Typed(null));
        Assert.Equal("", CatalogQuery.Typed("  select:path  "));
    }

    [Fact]
    public void AHiddenQuery_MustBeAnchored_OrItIsRefusedVisibly()
    {
        Assert.True(CatalogQuery.IsAnchored(Clients));
        Assert.True(CatalogQuery.IsAnchored("namespace:Fabrikam nodeType:Crm/Offer"));
        Assert.False(CatalogQuery.IsAnchored("nodeType:Crm/Offer scope:subtree"));
        Assert.False(CatalogQuery.IsAnchored("nodeType:Crm/Offer -namespace:Fabrikam partitions:some"));

        var refused = CatalogSearch.Of("nodeType:Crm/Offer").Render("nodeType:Crm/Offer", "", true, "Offers");
        Assert.IsType<MarkdownControl>(refused);
    }

    [Fact]
    public void AComputedFilter_NullIsTheEmptySentence_AndARepeatIsNotReRendered()
    {
        var filter = new Subject<string?>();
        var typed = new BehaviorSubject<string>("");
        var seen = Collect(CatalogSearch.Computed(filter, "Reply needed", "Card", whenEmpty: "Nothing is waiting on us.")
            .Section(typed, "Reply needed"));

        filter.OnNext(null);
        filter.OnNext(null);
        filter.OnNext($"nodeType:Crm/Counterparty id:fabrikam {"partitions:all"}");
        filter.OnNext($"nodeType:Crm/Counterparty id:fabrikam {"partitions:all"}");
        typed.OnNext("globex");
        typed.OnNext("globex");

        Assert.Equal(3, seen.Count);
        Assert.Contains("Nothing is waiting on us.", ((MarkdownControl)seen[0]).Markdown?.ToString());
        Assert.Equal("nodeType:Crm/Counterparty id:fabrikam partitions:all sort:subtreeLastModified-desc",
            ((MeshSearchControl)seen[1]).HiddenQuery);
        Assert.Equal("globex", ((MeshSearchControl)seen[2]).VisibleQuery);
    }

    [Fact]
    public void ACategoryWithAWhen_LeavesTheMenu_WhileItIsFalse()
    {
        var present = new BehaviorSubject<bool>(false);
        var app = new CatalogApp()
            .WithCategory("clients", "Clients", CatalogSearch.Of(Clients))
            .WithCategory(new CatalogCategory("space", "This space", [CatalogSearch.Of(Deals)]) { When = present })
            .WithoutQueryBar();

        var stack = app.Compose(NothingTyped, null);
        var sets = new List<string[]>();
        app.VisibleCategories().Subscribe(cs => sets.Add([.. cs.Select(c => c.Id)]));

        present.OnNext(true);
        present.OnNext(true);
        present.OnNext(false);

        Assert.Equal(["Menu"], stack.Areas.Select(a => a.Id?.ToString()));
        Assert.Equal([["clients"], ["clients", "space"], ["clients"]], sets);
    }
}
