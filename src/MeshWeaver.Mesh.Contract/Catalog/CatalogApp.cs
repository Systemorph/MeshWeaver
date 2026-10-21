using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// Namespace MeshWeaver.Layout, beside Controls.Catalog and CatalogSort — but this file lives in
// MeshWeaver.Mesh.Contract because the typed-text guard (CatalogQuery) must use the PRODUCTION query
// parser, and MeshWeaver.Layout cannot reference it (Mesh.Contract references Layout).
namespace MeshWeaver.Layout;

/// <summary>
/// One SEARCH of a catalog category: a hidden query (a <see cref="Filter"/>, or a COMPUTED one in
/// <see cref="ComputedFilter"/>) plus how its hits render. It becomes a <see cref="Controls.Catalog"/>
/// with the app's typed text as its visible query — nothing else.
/// </summary>
public record CatalogSearch
{
    /// <summary>A search over a fixed set. <paramref name="filter"/> is a query WITHOUT <c>sort:</c>
    /// that names where it looks (<c>namespace:</c>/<c>path:</c>/<c>partitions:all</c>).</summary>
    /// <param name="filter">The set, e.g. <c>nodeType:X scope:subtree partitions:all</c>.</param>
    /// <param name="title">The section title; null means the category's label.</param>
    /// <param name="itemArea">The area each hit renders through; null for the default card.</param>
    /// <param name="sort">The default order; null means <see cref="CatalogSort.LastActivity"/>.</param>
    public static CatalogSearch Of(string filter, string? title = null, string? itemArea = null, CatalogSort? sort = null)
        => new() { Filter = filter, Title = title, ItemArea = itemArea, Sort = sort };

    /// <summary>A search whose set is COMPUTED: each emission re-aims it; a null emission means the
    /// set is empty right now and renders <see cref="WhenEmpty"/> instead of running a query.</summary>
    /// <param name="filter">The filter stream; null emissions mean "nothing to show".</param>
    /// <param name="title">The section title; null means the category's label.</param>
    /// <param name="itemArea">The area each hit renders through; null for the default card.</param>
    /// <param name="sort">The default order; null means <see cref="CatalogSort.LastActivity"/>.</param>
    /// <param name="whenEmpty">The sentence shown while the filter is null.</param>
    public static CatalogSearch Computed(IObservable<string?> filter, string? title = null, string? itemArea = null,
        CatalogSort? sort = null, string? whenEmpty = null)
        => new() { ComputedFilter = filter, Title = title, ItemArea = itemArea, Sort = sort, WhenEmpty = whenEmpty };

    /// <summary>What the viewer sees in place of a refused (unanchored) section: the anchors it lacks, no query.</summary>
    public const string RefusedLabel = "⚠️ namespace: | path: | partitions:all";

    /// <summary>The fixed filter (no <c>sort:</c>). Exactly one of this and <see cref="ComputedFilter"/> is set.</summary>
    public string? Filter { get; init; }

    /// <summary>The computed filter; a null emission is the empty state.</summary>
    public IObservable<string?>? ComputedFilter { get; init; }

    /// <summary>The section title; null means the category's label.</summary>
    public string? Title { get; init; }

    /// <summary>The area each hit renders through; null for the default card.</summary>
    public string? ItemArea { get; init; }

    /// <summary>The default order; null means <see cref="CatalogSort.LastActivity"/>.</summary>
    public CatalogSort? Sort { get; init; }

    /// <summary>Hits per page before "Load more".</summary>
    public int PageSize { get; init; } = 20;

    /// <summary>Columns of the hit grid; null keeps the catalog's one hit per row.</summary>
    public int? MaxColumns { get; init; }

    /// <summary>The node type the section's "+" creates; null offers no "+".</summary>
    public string? CreateNodeType { get; init; }

    /// <summary>Where the "+" files the new node. Always state it for a subtree query.</summary>
    public string? CreateNamespace { get; init; }

    /// <summary>When set, the "+" shows only while this emits true (e.g. once the set has a member).</summary>
    public IObservable<bool>? CreateWhen { get; init; }

    /// <summary>The (localised) sentence shown while a computed filter is null; null shows a neutral dash.</summary>
    public string? WhenEmpty { get; init; }

    /// <summary>The last-mile hook over the composed control (grouping, section counts, render mode),
    /// applied AFTER everything above.</summary>
    public Func<MeshSearchControl, MeshSearchControl>? Configure { get; init; }

    /// <summary>Returns a copy whose "+" creates <paramref name="nodeType"/> in <paramref name="namespace"/>.</summary>
    /// <param name="nodeType">The node type to create.</param>
    /// <param name="namespace">Where the new node lands.</param>
    /// <param name="when">Optional: the "+" shows only while this emits true.</param>
    public CatalogSearch WithCreate(string nodeType, string? @namespace, IObservable<bool>? when = null)
        => this with { CreateNodeType = nodeType, CreateNamespace = @namespace, CreateWhen = when };

    /// <summary>Returns a copy laid out as a grid of <paramref name="columns"/>.</summary>
    /// <param name="columns">The column count.</param>
    public CatalogSearch WithMaxColumns(int columns) => this with { MaxColumns = columns };

    /// <summary>Returns a copy showing <paramref name="pageSize"/> hits per page.</summary>
    /// <param name="pageSize">Hits per page.</param>
    public CatalogSearch WithPageSize(int pageSize) => this with { PageSize = pageSize };

    /// <summary>Returns a copy with the last-mile hook <paramref name="configure"/>.</summary>
    /// <param name="configure">The hook over the composed control.</param>
    public CatalogSearch WithConfigure(Func<MeshSearchControl, MeshSearchControl> configure)
        => this with { Configure = configure };

    /// <summary>The filter as a stream: the fixed one once, or the computed one de-duplicated.</summary>
    public IObservable<string?> Filters() =>
        ComputedFilter?.DistinctUntilChanged() ?? Observable.Return(Filter);

    /// <summary>
    /// The section, live: re-emitted when the filter, the typed text or the "+" condition changes —
    /// and only then. Each search is its own area, so one search re-aiming never re-renders another.
    /// </summary>
    /// <param name="typed">What the reader typed into the app's bar, RAW — <see cref="Render"/> narrows it against this search's own hidden query.</param>
    /// <param name="categoryLabel">The category label, the title fallback.</param>
    /// <param name="refused">Told the details when a filter is refused (see <see cref="Render"/>).</param>
    public IObservable<UiControl> Section(IObservable<string> typed, string categoryLabel, Action<string>? refused = null) =>
        Filters()
            .CombineLatest(typed.DistinctUntilChanged(),
                // A silent condition hides the "+" — it never holds back the results.
                (CreateWhen?.StartWith(false) ?? Observable.Return(true)).DistinctUntilChanged(),
                (filter, text, create) => (filter, text, create))
            .Select(t => Render(t.filter, t.text, t.create, categoryLabel, refused));

    /// <summary>
    /// The section for one state. A null <paramref name="filter"/> is the empty sentence (a plain
    /// label, so localised text is never read as markup); a filter that names no anchor is REFUSED
    /// visibly rather than queried — the viewer sees THAT it was refused, never the hidden query,
    /// which goes to <paramref name="refused"/> (the log); otherwise it is
    /// <see cref="Controls.Catalog"/> with the bar hidden (the app owns the one bar) and the typed
    /// text, narrowed against THIS hidden query (<see cref="CatalogQuery.Narrowing"/>), as its
    /// visible query. Pure.
    /// </summary>
    /// <param name="filter">The current filter, or null for the empty state.</param>
    /// <param name="typed">What the reader typed, raw.</param>
    /// <param name="showCreate">Whether the "+" shows.</param>
    /// <param name="categoryLabel">The category label, the title fallback.</param>
    /// <param name="refused">Told the section title and the hidden query when the filter is refused.</param>
    public UiControl Render(string? filter, string typed, bool showCreate, string categoryLabel, Action<string>? refused = null)
    {
        var title = Title ?? categoryLabel;
        if (filter is null)
            // No literal English here (i18n): the caller's localised sentence, else a neutral dash.
            return Controls.Label(WhenEmpty ?? "—");
        var sort = Sort ?? CatalogSort.LastActivity;
        if (!CatalogQuery.IsAnchored(filter))
        {
            // Visible, not silent, language-neutral — and the hidden query stays hidden: it goes to the log.
            refused?.Invoke($"Catalog section '{title}' was not run: its hidden query names no namespace:, path:, basePath: or partitions:all anchor: {filter}");
            return Controls.Label(RefusedLabel);
        }

        var control = Controls.Catalog(title, filter, ItemArea, sort, PageSize)
            .WithShowSearchBox(false)
            .WithVisibleQuery(CatalogQuery.Narrowing(CatalogSort.Sorted(filter, sort.Sort), typed));
        if (MaxColumns is { } columns)
            control = control.WithMaxColumns(columns);
        if (showCreate && !string.IsNullOrWhiteSpace(CreateNodeType))
        {
            control = control.WithCreateNodeType(CreateNodeType!);
            if (!string.IsNullOrWhiteSpace(CreateNamespace))
                control = control.WithCreateNamespace(CreateNamespace!);
        }
        return Configure is null ? control : Configure(control);
    }
}

/// <summary>
/// One CATEGORY of a catalog app — a place the reader perceives as different, reached through the
/// app's menu. It holds one or more <see cref="CatalogSearch"/>es shown one under the other.
/// </summary>
/// <param name="Id">The stable, URL-safe id: the menu area's id and what a deep link names.</param>
/// <param name="Label">The menu label (localise it before passing it).</param>
/// <param name="Searches">The searches, in display order.</param>
public record CatalogCategory(string Id, string Label, ImmutableList<CatalogSearch> Searches)
{
    /// <summary>When set, the category is in the menu only while this emits true.</summary>
    public IObservable<bool>? When { get; init; }

    /// <summary>An optional control above the searches — e.g. a legend for what the hits' badges mean.</summary>
    public UiControl? Header { get; init; }
}

/// <summary>
/// A <b>catalog app</b>: N pluggable categories toggled by a menu, each delegating to
/// <see cref="Controls.Catalog"/> (MeshSearch) with its hidden queries, and ONE query bar shared by
/// all of them. What the reader types is appended after every hidden query of the active category
/// (the parser keeps the last <c>sort:</c>, so a typed order wins) and survives switching
/// categories; it can only NARROW each hidden query — <see cref="CatalogQuery.Narrowing"/> drops the
/// reserved qualifiers and checks the combination with the production parser.
/// <code>
/// new CatalogApp("Portfolio")
///     .WithCategory("clients", "Clients", CatalogSearch.Of(clients, itemArea: "Card"))
///     .WithCategory("deals", "Deals", CatalogSearch.Of(open, "Open"), CatalogSearch.Of(won, "Won"))
///     .Render(host)
/// </code>
/// Everything rendered is the platform's own controls: a <see cref="Controls.PageTitle"/>, a bound
/// <see cref="TextFieldControl"/>, a <see cref="TabsControl"/> and one <see cref="MeshSearchControl"/>
/// per search.
/// </summary>
/// <param name="Title">The page title; null shows none.</param>
public record CatalogApp(string? Title = null)
{
    /// <summary>The categories, in menu order.</summary>
    public ImmutableList<CatalogCategory> Categories { get; init; } = [];

    /// <summary>Whether the shared query bar shows. An embedded start tab hides it.</summary>
    public bool ShowQueryBar { get; init; } = true;

    /// <summary>The query bar's placeholder; null derives one from the title (localise before passing).</summary>
    public string? Placeholder { get; init; }

    /// <summary>The bar's placeholder when none is given: language-neutral (a glyph and the typed orders
    /// by example), so no viewer meets English they did not ask for. Pass a localised one for words.</summary>
    public static readonly string DefaultPlaceholder =
        "🔍 " + string.Join(", ", CatalogSort.WellKnown.Where(s => s != CatalogSort.LastActivity).Select(s => s.Sort));

    /// <summary>The layout-area data id the typed text is bound to. Change it only to host two apps in one area.</summary>
    public string QueryDataId { get; init; } = "catalogAppQuery";

    /// <summary>Adds a category of <paramref name="searches"/>.</summary>
    /// <param name="id">The stable, URL-safe id (menu area id, deep-link target).</param>
    /// <param name="label">The menu label.</param>
    /// <param name="searches">One or more searches.</param>
    public CatalogApp WithCategory(string id, string label, params CatalogSearch[] searches)
        => WithCategory(new CatalogCategory(id, label, [.. searches]));

    /// <summary>Adds <paramref name="category"/> — e.g. one carrying a <see cref="CatalogCategory.When"/>.</summary>
    /// <param name="category">The category.</param>
    public CatalogApp WithCategory(CatalogCategory category)
        => this with { Categories = Categories.Add(category) };

    /// <summary>Returns a copy without the shared query bar.</summary>
    public CatalogApp WithoutQueryBar() => this with { ShowQueryBar = false };

    /// <summary>Returns a copy whose bar shows <paramref name="placeholder"/>.</summary>
    /// <param name="placeholder">The placeholder text.</param>
    public CatalogApp WithPlaceholder(string placeholder) => this with { Placeholder = placeholder };

    /// <summary>
    /// The app, rendered into <paramref name="host"/>'s area: title, bar, menu. The selected category is
    /// <paramref name="selected"/>, else the area reference's id (<c>…/area/App/{id}</c>), else the first.
    /// </summary>
    /// <param name="host">The layout area host (the bar's text lives in its data section).</param>
    /// <param name="selected">The category id to open on; null reads it from the reference.</param>
    public StackControl Render(LayoutAreaHost host, string? selected = null)
    {
        IObservable<string> typed;
        if (ShowQueryBar)
        {
            // Seeded ONCE per host: an area that re-renders the app (an observable area re-emitting)
            // must not wipe what the reader has typed.
            if (Seeded.GetOrCreateValue(host).Add(QueryDataId))
                host.UpdateData(QueryDataId, "");
            typed = host.Stream.GetDataStream<string>(QueryDataId)
                .Select(text => text ?? "")
                .StartWith("")
                .DistinctUntilChanged();
        }
        else
            typed = Observable.Return("");
        var logger = host.Hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger<CatalogApp>();
        return Compose(typed, selected ?? CategoryIdOf(host.Reference.Id?.ToString()),
            logger is null ? null : details => logger.LogWarning("{Details}", details));
    }

    private static readonly ConditionalWeakTable<LayoutAreaHost, HashSet<string>> Seeded = new();

    /// <summary>The category a reference id names: the id without its URL query (<c>deals?x=1</c> → <c>deals</c>). Pure.</summary>
    /// <param name="referenceId">The layout-area reference's id, or null.</param>
    public static string? CategoryIdOf(string? referenceId)
    {
        if (string.IsNullOrWhiteSpace(referenceId))
            return null;
        var q = referenceId.IndexOf('?');
        var id = (q < 0 ? referenceId : referenceId[..q]).Trim('/');
        return id.Length == 0 ? null : id;
    }

    /// <summary>The app over an explicit <paramref name="typed"/> stream — what <see cref="Render"/>
    /// composes, without a host. Pure apart from the streams it is handed.</summary>
    /// <param name="typed">What the reader typed, raw.</param>
    /// <param name="selected">The category id to open on; null or unknown means the first.</param>
    /// <param name="refused">Told the details of a refused section.</param>
    public StackControl Compose(IObservable<string> typed, string? selected, Action<string>? refused = null)
    {
        var stack = Controls.Stack.WithWidth("100%");
        if (!string.IsNullOrWhiteSpace(Title))
            stack = stack.WithView(Controls.PageTitle(Title!), "Title");
        if (ShowQueryBar)
            stack = stack.WithView(QueryBar(), "Query");

        if (Categories.All(c => c.When is null))
            return stack.WithView(Menu(Categories, typed, selected, refused), "Menu");

        var visible = VisibleCategories().Select(cs => (UiControl?)Menu(cs, typed, selected, refused));
        return stack.WithView(visible, "Menu");
    }

    /// <summary>One category's page: its <see cref="CatalogCategory.Header"/>, then each search as its
    /// OWN live area (<c>Search0</c>, <c>Search1</c>, …). Pure apart from the streams it is handed.</summary>
    /// <param name="category">The category.</param>
    /// <param name="typed">What the reader typed, raw.</param>
    /// <param name="refused">Told the details of a refused section.</param>
    public static StackControl Body(CatalogCategory category, IObservable<string> typed, Action<string>? refused = null)
    {
        var body = Controls.Stack.WithWidth("100%");
        if (category.Header is not null)
            body = body.WithView(category.Header, "Header");
        for (var i = 0; i < category.Searches.Count; i++)
            body = body.WithView(category.Searches[i].Section(typed, category.Label, refused).Select(c => (UiControl?)c), $"Search{i}");
        return body;
    }

    /// <summary>The categories in the menu right now: each with no <see cref="CatalogCategory.When"/>
    /// always, the others while theirs emits true — a condition that has not answered yet counts as
    /// false, so it never holds back the rest. Re-emitted when one category's presence changes.</summary>
    public IObservable<ImmutableList<CatalogCategory>> VisibleCategories() =>
        Categories
            .Select(c => (c.When?.StartWith(false) ?? Observable.Return(true)).DistinctUntilChanged().Select(on => on ? c : null))
            .CombineLatest()
            .Select(cs => cs.Where(c => c is not null).Select(c => c!).ToImmutableList());

    /// <summary>The shared bar: a text field bound to <see cref="QueryDataId"/>, sending as the reader types. Pure.</summary>
    public TextFieldControl QueryBar() =>
        new TextFieldControl(new JsonPointerReference(""))
            .WithPlaceholder(Placeholder ?? DefaultPlaceholder)
            .WithWidth("100%")
            .WithImmediate(true)
            .WithImmediateDelay(300) with
        { DataContext = LayoutAreaReference.GetDataPointer(QueryDataId) };

    /// <summary>
    /// The menu: one tab per category, area id = the category id, label = its label, opened on
    /// <paramref name="selected"/> when it names a visible category. Each tab is a column of its
    /// searches, each search its own live area. Pure apart from the streams it is handed.
    /// </summary>
    /// <param name="categories">The categories to show.</param>
    /// <param name="typed">What the reader typed, raw.</param>
    /// <param name="selected">The category id to open on.</param>
    /// <param name="refused">Told the details of a refused section.</param>
    public static TabsControl Menu(IReadOnlyList<CatalogCategory> categories, IObservable<string> typed, string? selected,
        Action<string>? refused = null)
    {
        var tabs = Controls.Tabs.WithSkin(s => s.WithWidth("100%"));
        if (selected is not null && categories.Any(c => c.Id == selected))
            tabs = tabs.WithSkin(s => s.WithActiveTabId(selected));
        foreach (var category in categories)
        {
            var body = Body(category, typed, refused);
            var (id, label) = (category.Id, category.Label);
            // The area id is the stable category id (what a deep link names); the tab's label is
            // carried on its own TabSkin, so a localised label never changes the address.
            tabs = tabs.WithView(body, (Func<NamedAreaControl, NamedAreaControl>)(area => area.WithId(id).AddSkin(new TabSkin(label))));
        }
        return tabs;
    }
}

/// <summary>
/// The query rules of a catalog app — decided by the PRODUCTION <see cref="QueryParser"/>, never by a
/// look-alike: what the reader's typed text may add to a hidden query (only narrowing), and what a
/// hidden query must name (an anchor).
/// </summary>
public static partial class CatalogQuery
{
    private static readonly QueryParser Parser = new();

    /// <summary>The parser's reserved single-slot qualifiers except <c>sort:</c> — each anchors or shapes
    /// a query, and one typed into the bar would REPLACE the hidden query's own (the parser keeps one),
    /// so it is removed from the typed text whatever its sign (a negated <c>-scope:</c> is applied as a
    /// scope, not an exclusion). Derived from <see cref="QueryParser.ReservedQualifierNames"/>, so a new
    /// reserved qualifier is covered the day it is added.</summary>
    public static readonly FrozenSet<string> Removed = QueryParser.ReservedQualifierNames
        .Where(q => !q.Equals("sort", StringComparison.OrdinalIgnoreCase))
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What may follow <paramref name="hidden"/> of what the reader typed: <paramref name="typed"/>
    /// without the <see cref="Removed"/> qualifiers, PROVIDED the production parser reads
    /// <c>hidden + " " + result</c> as the hidden query and something more — the same anchor, scope,
    /// limit, source, projection and context, and the hidden filter's conjuncts still conjuncts of the
    /// whole. Anything that fails that (an <c>OR</c> that would reach past the hidden filter, an
    /// unbalanced group) keeps only its free text and <c>sort:</c>; if even that fails, nothing. Pure.
    /// </summary>
    /// <param name="hidden">The search's hidden query, as the view sends it (filter plus default order).</param>
    /// <param name="typed">What the reader typed, raw; null is empty.</param>
    public static string Narrowing(string hidden, string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed))
            return "";
        var stripped = Removed.Aggregate(typed, (t, q) => Parser.RemoveQualifier(t, q)).Trim();
        if (Narrows(hidden, stripped))
            return stripped;

        var fallback = string.Join(" ", new[] { Parser.Parse(stripped).TextSearch }
            .Concat(SortTerms().Matches(stripped).Select(m => m.Value))
            .Where(p => !string.IsNullOrWhiteSpace(p)));
        return Narrows(hidden, fallback) ? fallback : "";
    }

    /// <summary>Whether <paramref name="filter"/> says where it looks — exactly the production rule
    /// (<see cref="ParsedQuery.IsSufficientlySpecified"/>, MeshWeaver#4274). Pure.</summary>
    /// <param name="filter">A hidden query.</param>
    public static bool IsAnchored(string filter) => Parser.Parse(filter).IsSufficientlySpecified();

    /// <summary>Whether <paramref name="hidden"/> followed by <paramref name="typed"/> is the hidden
    /// query NARROWED, as the production parser reads both. Pure.</summary>
    /// <param name="hidden">The hidden query.</param>
    /// <param name="typed">The candidate typed text.</param>
    public static bool Narrows(string hidden, string typed)
    {
        if (string.IsNullOrWhiteSpace(typed))
            return true;
        ParsedQuery h, c;
        try
        {
            h = Parser.Parse(hidden);
            c = Parser.Parse($"{hidden} {typed}");
        }
        catch (FormatException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        return h.Path == c.Path && h.Scope == c.Scope && h.Limit == c.Limit && h.Source == c.Source
               && h.Context == c.Context && h.IsMain == c.IsMain && h.IsContent == c.IsContent
               && h.CrossPartition == c.CrossPartition
               && Same(h.Select, c.Select) && Same(h.Paths, c.Paths)
               && ConjunctsKept(h.Filter, c.Filter);
    }

    private static bool Same(IReadOnlyList<string>? a, IReadOnlyList<string>? b) =>
        (a is null && b is null) || (a is not null && b is not null && a.SequenceEqual(b, StringComparer.Ordinal));

    /// <summary>Every conjunct of the hidden filter is, in order, a leading conjunct of the combined one.</summary>
    private static bool ConjunctsKept(QueryNode? hidden, QueryNode? combined)
    {
        if (hidden is null)
            return true;
        if (combined is null)
            return false;
        var h = Conjuncts(hidden);
        var c = Conjuncts(combined);
        return c.Count >= h.Count && h.Select((n, i) => NodeEquals(n, c[i])).All(x => x);
    }

    private static IReadOnlyList<QueryNode> Conjuncts(QueryNode node) =>
        node is QueryAnd and ? and.Children : [node];

    private static bool NodeEquals(QueryNode a, QueryNode b) => (a, b) switch
    {
        (QueryComparison x, QueryComparison y) =>
            x.Condition.Selector.Equals(y.Condition.Selector, StringComparison.OrdinalIgnoreCase)
            && x.Condition.Operator == y.Condition.Operator
            && x.Condition.Values.SequenceEqual(y.Condition.Values, StringComparer.Ordinal),
        (QueryAnd x, QueryAnd y) => Pairwise(x.Children, y.Children),
        (QueryOr x, QueryOr y) => Pairwise(x.Children, y.Children),
        _ => false,
    };

    private static bool Pairwise(IReadOnlyList<QueryNode> a, IReadOnlyList<QueryNode> b) =>
        a.Count == b.Count && a.Zip(b).All(p => NodeEquals(p.First, p.Second));

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<=^|\s)sort:\S+", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex SortTerms();
}
