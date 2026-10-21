using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Mesh;
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
    public void ACategoryHeader_SitsAboveItsSearches()
    {
        var category = new CatalogCategory("deals", "Deals", [CatalogSearch.Of(Deals, "Open"), CatalogSearch.Of(Deals, "Won")])
        {
            Header = Controls.Markdown("legend"),
        };

        var body = CatalogApp.Body(category, NothingTyped);

        Assert.Equal(["Header", "Search0", "Search1"], body.Areas.Select(a => a.Id?.ToString()));
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

    private const string Hidden = Deals + " sort:subtreeLastModified-desc";

    [Fact]
    public void TypedText_CanOnlyNarrow_ReservedQualifiersAreDropped_WhateverTheirSign()
    {
        Assert.Equal("fabrikam sort:name content.offering:X",
            CatalogQuery.Narrowing(Hidden, "fabrikam namespace:Globex sort:name partitions:all content.offering:X scope:descendants"));
        // A negated shaping qualifier is APPLIED, not excluded (-scope:subtree widens a children scope).
        Assert.Equal("fabrikam", CatalogQuery.Narrowing(Hidden, "fabrikam -scope:subtree -source:all"));
        // is: and limit: are single-slot too: a typed one would replace the hidden is:main / limit:.
        Assert.Equal("fabrikam", CatalogQuery.Narrowing(Hidden + " is:main limit:5", "fabrikam is:content limit:1000"));
        Assert.Equal("", CatalogQuery.Narrowing(Hidden, null));
        Assert.Equal("", CatalogQuery.Narrowing(Hidden, "  select:path  "));
    }

    [Fact]
    public void AGroupedReservedQualifier_IsDroppedToo()
    {
        var typed = CatalogQuery.Narrowing("namespace:Fabrikam nodeType:Crm/Offer sort:name", "(namespace:Globex)");
        var parsed = new QueryParser().Parse("namespace:Fabrikam nodeType:Crm/Offer sort:name " + typed);

        Assert.DoesNotContain("Globex", typed);
        Assert.Equal(new QueryParser().Parse("namespace:Fabrikam nodeType:Crm/Offer").Path, parsed.Path);
    }

    [Fact]
    public void AnOrThatWouldReachPastTheHiddenFilter_KeepsOnlyFreeTextAndSort()
    {
        // (hidden AND status:Open) OR status:Won would admit Won nodes of ANY type.
        Assert.Equal("", CatalogQuery.Narrowing(Hidden, "content.status:Open OR content.status:Won"));
        Assert.Equal("", CatalogQuery.Narrowing(Hidden, "OR nodeType:Other"));
        Assert.Equal("fabrikam sort:name", CatalogQuery.Narrowing(Hidden, "fabrikam sort:name OR nodeType:Other"));
        // A grouped OR stays inside the conjunction and is kept.
        var grouped = CatalogQuery.Narrowing(Hidden, "(content.status:Open OR content.status:Won)");
        Assert.True(CatalogQuery.Narrows(Hidden, grouped));
        Assert.Contains("content.status:Won", grouped);
    }

    [Fact]
    public void TheDropSet_IsTheParsersReservedQualifiers_ExceptSort()
    {
        var expected = QueryParser.ReservedQualifierNames.Where(q => !q.Equals("sort", StringComparison.OrdinalIgnoreCase))
            .Select(q => q.ToLowerInvariant()).Order();
        Assert.Equal(expected, CatalogQuery.Removed.Select(q => q.ToLowerInvariant()).Order());
        Assert.True(CatalogQuery.Removed.Contains("is"));
        Assert.True(CatalogQuery.Removed.Contains("limit"));
    }

    [Fact]
    public void AHiddenQuery_MustBeAnchored_ByTheProductionRule_OrItIsRefusedVisibly()
    {
        Assert.True(CatalogQuery.IsAnchored(Clients));
        Assert.True(CatalogQuery.IsAnchored("namespace:Fabrikam nodeType:Crm/Offer"));
        Assert.False(CatalogQuery.IsAnchored("nodeType:Crm/Offer scope:subtree"));
        Assert.False(CatalogQuery.IsAnchored("path:* nodeType:Crm/Offer"));
        Assert.False(CatalogQuery.IsAnchored("nodeType:Crm/Offer -namespace:Fabrikam"));

        var logged = new List<string>();
        var refused = CatalogSearch.Of("nodeType:Crm/Offer").Render("nodeType:Crm/Offer", "", true, "Offers", logged.Add);
        // The viewer sees THAT it was refused, never the hidden query; the log gets the details.
        var label = Assert.IsType<LabelControl>(refused);
        Assert.Equal(CatalogSearch.RefusedLabel, label.Data);
        Assert.DoesNotContain("nodeType", label.Data?.ToString());
        Assert.Contains("nodeType:Crm/Offer", Assert.Single(logged));
    }

    [Fact]
    public void ASilentCreateCondition_HidesThePlus_ButNeverHoldsBackTheResults()
    {
        var when = new Subject<bool>();
        var seen = Collect(CatalogSearch.Of(Deals, "Open").WithCreate("Crm/Opportunity", "Fabrikam", when).Section(NothingTyped, "Deals"));

        Assert.Single(seen);
        Assert.Null(((MeshSearchControl)seen[0]).CreateNodeType);
        when.OnNext(true);
        Assert.Equal("Crm/Opportunity", ((MeshSearchControl)seen[^1]).CreateNodeType);
    }

    [Fact]
    public void ASilentCategoryCondition_NeverHoldsBackTheOtherCategories()
    {
        var app = new CatalogApp()
            .WithCategory("clients", "Clients", CatalogSearch.Of(Clients))
            .WithCategory(new CatalogCategory("space", "This space", [CatalogSearch.Of(Deals)]) { When = new Subject<bool>() });
        var sets = new List<string[]>();
        app.VisibleCategories().Subscribe(cs => sets.Add([.. cs.Select(c => c.Id)]));

        Assert.Equal([["clients"]], sets);
    }

    [Fact]
    public void TheDeepLink_IsTheReferenceId_WithoutItsUrlQuery()
    {
        Assert.Equal("deals", CatalogApp.CategoryIdOf("deals?x=1"));
        Assert.Equal("deals", CatalogApp.CategoryIdOf("deals"));
        Assert.Null(CatalogApp.CategoryIdOf(null));
        Assert.Null(CatalogApp.CategoryIdOf("?x=1"));
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
        // A plain label: localised text with _ or * is shown as written, never read as markup.
        Assert.Equal("Nothing is waiting on us.", ((LabelControl)seen[0]).Data);
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
