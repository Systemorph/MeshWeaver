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
        // An error arm: a retained stream whose owner is disposed at the end of a test terminates its
        // late subscribers with ObjectDisposedException, which must not escape as an unhandled fault.
        source.Subscribe(seen.Add, _ => { });
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
    public void AGroupedHiddenConjunction_StillAcceptsATypedFilter()
    {
        const string grouped = "partitions:all (nodeType:Crm/Opportunity content.isOpen:true) sort:name";

        Assert.Equal("content.status:Open", CatalogQuery.Narrowing(grouped, "content.status:Open"));
        Assert.Equal("", CatalogQuery.Narrowing(grouped, "content.status:Open OR content.status:Won"));
    }

    [Fact]
    public void AGroupEmptiedByStripping_DoesNotSwallowTheFilterAfterIt()
    {
        const string hidden = "partitions:all nodeType:Crm/Opportunity sort:name";

        var typed = CatalogQuery.Narrowing(hidden, "(namespace:Globex) content.status:Open");
        var parsed = new QueryParser().Parse($"{hidden} {typed}");

        Assert.Equal("content.status:Open", typed);
        Assert.Contains(Comparisons(parsed.Filter), c => c.Selector == "content.status");
        // A typed filter the parser would silently drop is not accepted as narrowing — an empty group
        // or a text-only group stops the parser's AND and swallows the term after it.
        Assert.False(CatalogQuery.Narrows(hidden, "() content.status:Open"));
        Assert.False(CatalogQuery.Narrows(hidden, "(fabrikam) content.status:Open"));
        Assert.True(CatalogQuery.Narrows(hidden, "fabrikam content.status:Open"));
    }

    private static IEnumerable<QueryCondition> Comparisons(QueryNode? node) => node switch
    {
        QueryComparison c => [c.Condition],
        QueryAnd and => and.Children.SelectMany(Comparisons),
        QueryOr or => or.Children.SelectMany(Comparisons),
        _ => [],
    };

    [Fact]
    public void ParenthesesInsideATerm_AreTheTermsOwn()
    {
        const string hidden = "partitions:all nodeType:Crm/Opportunity sort:name";

        // A list operand and a value carrying "()" are terms, not expression groups.
        Assert.Equal("content.status:(Open OR Won)", CatalogQuery.Narrowing(hidden, "content.status:(Open OR Won)"));
        Assert.Equal("content.code:foo()", CatalogQuery.Narrowing(hidden, "content.code:foo()"));
        Assert.Equal("content.code:foo() content.status:Open",
            CatalogQuery.Narrowing(hidden, "(namespace:Globex) content.code:foo() content.status:Open"));
    }

    [Fact]
    public void ANamespaceAlternation_StillAcceptsATypedFilter()
    {
        // The parser appends the namespace membership AFTER the ordinary filters.
        const string hidden = "namespace:Fabrikam/Deals|Globex/Deals nodeType:Crm/Opportunity sort:name";

        Assert.Equal("content.status:Open", CatalogQuery.Narrowing(hidden, "content.status:Open"));
        Assert.Equal("", CatalogQuery.Narrowing(hidden, "content.status:Open OR content.status:Won"));
    }

    [Fact]
    public void ARetainedApp_ResumesAComputedFilter_WhenTheMenuIsRebuilt()
    {
        var filter = new Subject<string?>();
        var app = new CatalogApp().WithCategory("reply", "Reply needed", CatalogSearch.Computed(filter, itemArea: "Card"));
        using var owner = new System.Reactive.Disposables.CompositeDisposable();
        var search = app.Retained(owner, new MeshWeaver.Messaging.ReleaseLane()).Categories[0].Searches[0];

        var first = new List<UiControl>();
        var subscription = search.Section(NothingTyped, "Reply needed").Subscribe(first.Add, _ => { });
        filter.OnNext("nodeType:Crm/Counterparty id:fabrikam partitions:all");
        subscription.Dispose();   // the menu is rebuilt: its sections are torn down and re-subscribed

        var second = Collect(search.Section(NothingTyped, "Reply needed"));

        // Loading until the filter answered, then the search — and the rebuilt section resumes from the
        // retained filter at once, so its loading frame is immediately replaced.
        Assert.Equal(2, first.Count);
        Assert.IsType<ProgressControl>(first[0]);
        Assert.Equal(2, second.Count);
        var resumed = Assert.IsType<MeshSearchControl>(second[1]);
        Assert.StartsWith("nodeType:Crm/Counterparty id:fabrikam", resumed.HiddenQuery?.ToString());
    }

    [Fact]
    public void TheParsersReservedNames_CannotBeMutated()
    {
        Assert.IsNotType<HashSet<string>>(QueryParser.ReservedQualifierNames);
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

        Assert.Equal(4, seen.Count);
        Assert.IsType<ProgressControl>(seen[0]);
        // A plain label: localised text with _ or * is shown as written, never read as markup.
        Assert.Equal("Nothing is waiting on us.", ((LabelControl)seen[1]).Data);
        Assert.Equal("nodeType:Crm/Counterparty id:fabrikam partitions:all sort:subtreeLastModified-desc",
            ((MeshSearchControl)seen[2]).HiddenQuery);
        Assert.Equal("globex", ((MeshSearchControl)seen[3]).VisibleQuery);
    }

    [Fact]
    public void AComputedSection_IsLoading_NeverAbsent_UntilItsFilterAnswers()
    {
        // A filter backed by a live read that has not answered yet. Before, the section emitted
        // NOTHING here, so its area never existed: an empty tab to the reader, and "missing" to a
        // one-shot read of the page (Crm Incidents, 2026-10-21).
        var silent = new Subject<string?>();

        var plain = Collect(CatalogSearch.Computed(silent, "Reply needed", "Card").Section(NothingTyped, "Reply needed"));
        var worded = Collect(CatalogSearch.Computed(silent, "Reply needed", "Card", null, null, "Reading the journeys…")
            .Section(NothingTyped, "Reply needed"));

        var indicator = Assert.IsType<ProgressControl>(Assert.Single(plain));
        Assert.Equal("", indicator.Message);   // no literal English from the platform (i18n)
        Assert.Equal("Reading the journeys…", Assert.IsType<ProgressControl>(Assert.Single(worded)).Message);

        // A FIXED search answers at once and never shows the indicator.
        Assert.IsType<MeshSearchControl>(Assert.Single(Collect(CatalogSearch.Of(Clients).Section(NothingTyped, "Clients"))));
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
