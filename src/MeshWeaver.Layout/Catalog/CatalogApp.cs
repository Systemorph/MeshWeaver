using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Layout.Composition;

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
    /// <param name="typed">The app's typed text (already through <see cref="CatalogQuery.Typed"/>).</param>
    /// <param name="categoryLabel">The category label, the title fallback.</param>
    public IObservable<UiControl> Section(IObservable<string> typed, string categoryLabel) =>
        Filters()
            .CombineLatest(typed.DistinctUntilChanged(),
                (CreateWhen ?? Observable.Return(true)).DistinctUntilChanged(),
                (filter, text, create) => (filter, text, create))
            .Select(t => Render(t.filter, t.text, t.create, categoryLabel));

    /// <summary>
    /// The section for one state. A null <paramref name="filter"/> is the empty sentence; a filter
    /// that names no anchor is REFUSED visibly rather than queried; otherwise it is
    /// <see cref="Controls.Catalog"/> with the bar hidden (the app owns the one bar) and
    /// <paramref name="typed"/> as its visible query. Pure.
    /// </summary>
    /// <param name="filter">The current filter, or null for the empty state.</param>
    /// <param name="typed">The sanitised typed text.</param>
    /// <param name="showCreate">Whether the "+" shows.</param>
    /// <param name="categoryLabel">The category label, the title fallback.</param>
    public UiControl Render(string? filter, string typed, bool showCreate, string categoryLabel)
    {
        var title = Title ?? categoryLabel;
        if (filter is null)
            // No literal English here (i18n): the caller's localised sentence, else a neutral dash.
            return Controls.Markdown(WhenEmpty is null ? "—" : $"_{WhenEmpty}_");
        if (!CatalogQuery.IsAnchored(filter))
            // Visible, not silent, and language-neutral: the anchors it lacks, then the query.
            return Controls.Markdown($"⚠️ `namespace:` | `path:` | `partitions:all` ∉ `{filter}`");

        var control = Controls.Catalog(title, filter, ItemArea, Sort, PageSize)
            .WithShowSearchBox(false)
            .WithVisibleQuery(typed);
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
}

/// <summary>
/// A <b>catalog app</b>: N pluggable categories toggled by a menu, each delegating to
/// <see cref="Controls.Catalog"/> (MeshSearch) with its hidden queries, and ONE query bar shared by
/// all of them. What the reader types is appended after every hidden query of the active category
/// (the parser keeps the last <c>sort:</c>, so a typed order wins) and survives switching
/// categories; terms that would re-anchor a query (<c>namespace:</c>, <c>path:</c>, <c>scope:</c>,
/// <c>partitions:</c>, …) are dropped from it first, so typing can only NARROW (<see cref="CatalogQuery.Typed"/>).
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
            host.UpdateData(QueryDataId, "");
            typed = host.Stream.GetDataStream<string>(QueryDataId)
                .Select(CatalogQuery.Typed)
                .StartWith("")
                .DistinctUntilChanged();
        }
        else
            typed = Observable.Return("");
        return Compose(typed, selected ?? host.Reference.Id?.ToString());
    }

    /// <summary>The app over an explicit <paramref name="typed"/> stream — what <see cref="Render"/>
    /// composes, without a host. Pure apart from the streams it is handed.</summary>
    /// <param name="typed">The sanitised typed text.</param>
    /// <param name="selected">The category id to open on; null or unknown means the first.</param>
    public StackControl Compose(IObservable<string> typed, string? selected)
    {
        var stack = Controls.Stack.WithWidth("100%");
        if (!string.IsNullOrWhiteSpace(Title))
            stack = stack.WithView(Controls.PageTitle(Title!), "Title");
        if (ShowQueryBar)
            stack = stack.WithView(QueryBar(), "Query");

        if (Categories.All(c => c.When is null))
            return stack.WithView(Menu(Categories, typed, selected), "Menu");

        var visible = VisibleCategories().Select(cs => (UiControl?)Menu(cs, typed, selected));
        return stack.WithView(visible, "Menu");
    }

    /// <summary>The categories in the menu right now: each with no <see cref="CatalogCategory.When"/>
    /// always, the others while theirs emits true — re-emitted when one category's presence changes.</summary>
    public IObservable<ImmutableList<CatalogCategory>> VisibleCategories() =>
        Categories
            .Select(c => (c.When ?? Observable.Return(true)).DistinctUntilChanged().Select(on => on ? c : null))
            .CombineLatest()
            .Select(cs => cs.Where(c => c is not null).Select(c => c!).ToImmutableList());

    /// <summary>The shared bar: a text field bound to <see cref="QueryDataId"/>, sending as the reader types. Pure.</summary>
    public TextFieldControl QueryBar() =>
        new TextFieldControl(new JsonPointerReference(""))
            .WithPlaceholder(Placeholder ?? CatalogSort.Placeholder(Title ?? "this catalog", CatalogSort.LastActivity))
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
    /// <param name="typed">The sanitised typed text.</param>
    /// <param name="selected">The category id to open on.</param>
    public static TabsControl Menu(IReadOnlyList<CatalogCategory> categories, IObservable<string> typed, string? selected)
    {
        var tabs = Controls.Tabs.WithSkin(s => s.WithWidth("100%"));
        if (selected is not null && categories.Any(c => c.Id == selected))
            tabs = tabs.WithSkin(s => s.WithActiveTabId(selected));
        foreach (var category in categories)
        {
            var body = Controls.Stack.WithWidth("100%");
            for (var i = 0; i < category.Searches.Count; i++)
                body = body.WithView(category.Searches[i].Section(typed, category.Label).Select(c => (UiControl?)c), $"Search{i}");
            var (id, label) = (category.Id, category.Label);
            // The area id is the stable category id (what a deep link names); the tab's label is
            // carried on its own TabSkin, so a localised label never changes the address.
            tabs = tabs.WithView(body, (Func<NamedAreaControl, NamedAreaControl>)(area => area.WithId(id).AddSkin(new TabSkin(label))));
        }
        return tabs;
    }
}

/// <summary>The query rules of a catalog app: what typing may add, and what a hidden query must name.</summary>
public static class CatalogQuery
{
    /// <summary>Qualifiers that ANCHOR or SHAPE a query rather than narrow it. A positive one typed into
    /// the bar would replace the hidden query's own (the parser keeps one), so it is dropped.</summary>
    public static readonly IReadOnlySet<string> Anchoring = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "namespace", "path", "scope", "partitions", "basePath", "source", "context", "select",
    };

    /// <summary>
    /// <paramref name="text"/> with every POSITIVE anchoring term removed (<see cref="Anchoring"/>), so
    /// the typed text can only narrow a hidden query: free text, field filters, negations and
    /// <c>sort:</c> pass. Quotes are respected. Null becomes empty. Pure.
    /// </summary>
    /// <param name="text">What the reader typed.</param>
    public static string Typed(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        return string.Join(" ", Tokens(text).Where(t => !IsAnchoringTerm(t)));
    }

    /// <summary>Whether <paramref name="filter"/> says where it looks: a positive <c>namespace:</c>,
    /// <c>path:</c>, <c>basePath:</c> or <c>partitions:all</c> term (MeshWeaver#4274). Pure.</summary>
    /// <param name="filter">A hidden query.</param>
    public static bool IsAnchored(string filter) =>
        Tokens(filter).Any(t =>
        {
            var (field, value) = Split(t);
            return field is not null
                   && (field.Equals("namespace", StringComparison.OrdinalIgnoreCase)
                       || field.Equals("path", StringComparison.OrdinalIgnoreCase)
                       || field.Equals("basePath", StringComparison.OrdinalIgnoreCase)
                       || (field.Equals("partitions", StringComparison.OrdinalIgnoreCase)
                           && value.Equals("all", StringComparison.OrdinalIgnoreCase)))
                   && value.Length > 0;
        });

    private static bool IsAnchoringTerm(string token)
    {
        var (field, _) = Split(token);
        return field is not null && Anchoring.Contains(field);
    }

    /// <summary>A POSITIVE <c>field:value</c> term's field and value; (null, "") for anything else.</summary>
    private static (string? Field, string Value) Split(string token)
    {
        if (token.StartsWith('-') || token.StartsWith('"') || token.StartsWith('\''))
            return (null, "");
        var t = token.TrimStart('+');
        var colon = t.IndexOf(':');
        return colon <= 0 ? (null, "") : (t[..colon], t[(colon + 1)..]);
    }

    /// <summary>Whitespace-separated tokens; a quoted run stays inside its token.</summary>
    private static IEnumerable<string> Tokens(string text)
    {
        var start = -1;
        char? quote = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote is not null)
            {
                if (c == quote) quote = null;
                continue;
            }
            if (c is '"' or '\'')
            {
                if (start < 0) start = i;
                quote = c;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                if (start >= 0) yield return text[start..i];
                start = -1;
            }
            else if (start < 0)
                start = i;
        }
        if (start >= 0) yield return text[start..];
    }
}
