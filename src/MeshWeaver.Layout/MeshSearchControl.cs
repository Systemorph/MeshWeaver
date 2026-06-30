using MeshWeaver.Layout.Catalog;

namespace MeshWeaver.Layout;

/// <summary>
/// Render mode for MeshSearchControl determining how results are displayed.
/// </summary>
public enum MeshSearchRenderMode
{
    /// <summary>
    /// Flat grid display - all results shown as thumbnail cards.
    /// </summary>
    Flat,

    /// <summary>
    /// One result per row — a vertical list (icon · title · description), ordered by relevance.
    /// The row shows the node's description, falling back to a "create a description" prompt when
    /// none exists. Used by the global search results page.
    /// </summary>
    List,

    /// <summary>
    /// Hierarchical display - tree structure with parent-child indentation.
    /// Each root node and its subtree kept in a single grid cell.
    /// </summary>
    Hierarchical,

    /// <summary>
    /// Grouped by category - results grouped under category headings.
    /// </summary>
    Grouped,

    /// <summary>
    /// Namespace catalog - results organized by their namespace hierarchy.
    /// Sub-namespaces render as nested collapsible sections (with counts);
    /// nodes render as thumbnail cards. Levels load lazily: only the direct
    /// children of the root namespace are queried up front, deeper levels are
    /// queried on folder expand. Typing in the search box switches to a
    /// subtree search whose results are grouped by relative namespace.
    /// </summary>
    NamespaceTree,

    /// <summary>
    /// Re-rooting graph navigator — navigate the mesh along its edges. For the current node
    /// it shows the ancestors <b>above</b> (a clickable breadcrumb rail) and the next populated
    /// level <b>below</b> (the nearest real nodes, skipping empty intermediate namespace
    /// segments) as a card grid. Both come from a single live query each
    /// (<c>scope:ancestors</c> above, <c>scope:nextLevel</c> below). Clicking a card or an
    /// ancestor re-roots the view there and recomputes both — "navigate → visualize → navigate".
    /// </summary>
    GraphNavigator
}

/// <summary>
/// A control that provides a configurable search with results displayed in a LayoutGrid.
/// Supports hidden query parts (always applied), visible query (user-modifiable),
/// and different render modes (flat, hierarchical, grouped).
/// </summary>
public record MeshSearchControl()
    : UiControl<MeshSearchControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>
    /// Section title displayed inline with the search bar and create button.
    /// </summary>
    public object? Title { get; init; }

    /// <summary>
    /// The hidden query part that is always applied (e.g., namespace:X scope:descendants).
    /// Not shown to the user but combined with VisibleQuery for search.
    /// </summary>
    public object? HiddenQuery { get; init; }

    /// <summary>
    /// The visible/modifiable query part (search term the user can edit).
    /// This is what appears in the search box.
    /// </summary>
    public object? VisibleQuery { get; init; }

    /// <summary>
    /// Placeholder text for the search box.
    /// </summary>
    public object? Placeholder { get; init; }

    /// <summary>
    /// The namespace for search scope (used for autocomplete).
    /// </summary>
    public object? Namespace { get; init; }

    /// <summary>
    /// The render mode: Flat, Hierarchical, or Grouped.
    /// </summary>
    public object? RenderMode { get; init; }

    /// <summary>
    /// Maximum columns in the grid (default 3).
    /// </summary>
    public object? MaxColumns { get; init; }

    /// <summary>
    /// Whether to show the search box (default true).
    /// Set to false to just show results without search input.
    /// </summary>
    public object? ShowSearchBox { get; init; }

    /// <summary>
    /// Whether to show the discreet view-options bar above the results (default false).
    /// When true, a small "Group by" combobox (None / Type / Namespace / Category) and a
    /// display menu (show/hide the search bar and section counts) are rendered. Opt-in so
    /// the many embedded usages of the search control are unaffected. Only meaningful for
    /// the Flat and Grouped render modes.
    /// </summary>
    public object? ShowViewOptions { get; init; }

    /// <summary>
    /// Whether to exclude the base path node from results (default true).
    /// When true, the node at the namespace path itself is not shown.
    /// </summary>
    public object? ExcludeBasePath { get; init; }

    /// <summary>
    /// Whether results should update live as user types (default true).
    /// When false, search only triggers on Enter.
    /// </summary>
    public object? LiveSearch { get; init; }

    /// <summary>
    /// Configuration for grouping results by a property.
    /// </summary>
    public GroupingConfig? Grouping { get; init; }

    /// <summary>
    /// Configuration for section display (counts, limits, collapsibility).
    /// </summary>
    public SectionConfig? Sections { get; init; }

    /// <summary>
    /// Configuration for sorting results.
    /// </summary>
    public SortConfig? Sorting { get; init; }

    /// <summary>
    /// Configuration for responsive grid layout.
    /// </summary>
    public GridConfig? Grid { get; init; }

    /// <summary>
    /// Whether to use Query for reactive updates (default false).
    /// When true, results automatically update when underlying data changes.
    /// </summary>
    public object? ReactiveMode { get; init; }

    /// <summary>
    /// When set, each search result item is rendered via a LayoutAreaView
    /// pointing to this area name (e.g., "Thumbnail") instead of the default FluentCard.
    /// </summary>
    public object? ItemArea { get; init; }

    /// <summary>
    /// When true, clicking a card does not navigate to /{path}.
    /// Use this when the card content has interactive elements (buttons, etc.).
    /// </summary>
    public object? DisableNavigation { get; init; }

    /// <summary>
    /// Whether to show the "No items found." message when there are no results (default true).
    /// Set to false to render nothing when the search returns no items.
    /// </summary>
    public object? ShowEmptyMessage { get; init; }

    /// <summary>
    /// Whether to show a loading indicator (skeleton cards) while results are loading (default true).
    /// Set to false for secondary/embedded sections like Children, Comments, etc.
    /// </summary>
    public object? ShowLoadingIndicator { get; init; }

    /// <summary>
    /// Pre-computed grouped search results. When set, the Blazor component
    /// uses these groups directly instead of computing them from lambdas.
    /// This is the serializable output of ProcessResults().
    /// </summary>
    public GroupedSearchResult? PrecomputedGroups { get; init; }

    /// <summary>
    /// When set, a "+" button is shown. Clicking it creates a new transient node
    /// of this type and navigates to the Create area.
    /// </summary>
    public object? CreateNodeType { get; init; }

    /// <summary>
    /// Namespace where new nodes are created. If not set, derived from HiddenQuery's namespace: prefix.
    /// </summary>
    public object? CreateNamespace { get; init; }

    /// <summary>
    /// When set, a "+" button is shown that navigates directly to this URL.
    /// Takes priority over CreateNodeType (no transient node is created).
    /// </summary>
    public object? CreateHref { get; init; }

    /// <summary>
    /// When set, clicking a result posts a ClickMessage to this address
    /// with the clicked node's path, instead of navigating the browser.
    /// The receiving hub handles the message (e.g., side panel navigation).
    /// </summary>
    public object? ClickMessageAddress { get; init; }

    /// <summary>
    /// When set (e.g. <c>"Search"</c>), each result card and each namespace
    /// folder shows a secondary "Drill down" affordance that navigates to
    /// <c>/{path}/{DrillDownArea}</c> — the re-rooted Search/catalog area, so the
    /// user can keep browsing INTO that node's namespace. The PRIMARY click still
    /// opens the node's default page <c>/{path}</c> (empty area — never a hardcoded
    /// "Overview"). When null/unset, no drill-down affordance is rendered and the
    /// catalog behaves exactly as before (opt-in).
    /// </summary>
    public object? DrillDownArea { get; init; }

    // Basic fluent methods
    /// <summary>Returns a copy with <paramref name="title"/> as its section title.</summary>
    /// <param name="title">The section title displayed inline with the search bar.</param>
    public MeshSearchControl WithTitle(string title) => this with { Title = title };
    /// <summary>Returns a copy with <paramref name="address"/> as the click-message target address.</summary>
    /// <param name="address">The hub address to post a ClickMessage to when a result is clicked.</param>
    public MeshSearchControl WithClickMessageAddress(object address) => this with { ClickMessageAddress = address };
    /// <summary>Returns a copy with <paramref name="query"/> as its hidden (always-applied) query fragment.</summary>
    /// <param name="query">The hidden query fragment, e.g. <c>namespace:X scope:descendants</c>.</param>
    public MeshSearchControl WithHiddenQuery(string query) => this with { HiddenQuery = query };
    /// <summary>Returns a copy with <paramref name="query"/> as its user-visible, editable query.</summary>
    /// <param name="query">The initial value shown in the search box.</param>
    public MeshSearchControl WithVisibleQuery(string query) => this with { VisibleQuery = query };
    /// <summary>Returns a copy with <paramref name="placeholder"/> as the search-box placeholder text.</summary>
    /// <param name="placeholder">The placeholder string displayed when the search box is empty.</param>
    public MeshSearchControl WithPlaceholder(string placeholder) => this with { Placeholder = placeholder };
    /// <summary>Returns a copy with <paramref name="ns"/> as the namespace for search scope and autocomplete.</summary>
    /// <param name="ns">The namespace path.</param>
    public MeshSearchControl WithNamespace(string ns) => this with { Namespace = ns };
    /// <summary>Returns a copy with <paramref name="mode"/> as its render mode.</summary>
    /// <param name="mode">One of Flat, Hierarchical, Grouped, NamespaceTree, or GraphNavigator.</param>
    public MeshSearchControl WithRenderMode(MeshSearchRenderMode mode) => this with { RenderMode = mode };
    /// <summary>Returns a copy with <paramref name="columns"/> as the maximum grid column count.</summary>
    /// <param name="columns">Maximum number of grid columns; default 3.</param>
    public MeshSearchControl WithMaxColumns(int columns) => this with { MaxColumns = columns };
    /// <summary>Returns a copy with the search-box visibility set to <paramref name="show"/>.</summary>
    /// <param name="show"><c>false</c> hides the search box, showing only results.</param>
    public MeshSearchControl WithShowSearchBox(bool show) => this with { ShowSearchBox = show };
    /// <summary>Returns a copy with the view-options bar enabled or disabled.</summary>
    /// <param name="show"><c>true</c> renders the Group-by combobox and display-menu above results.</param>
    public MeshSearchControl WithViewOptions(bool show = true) => this with { ShowViewOptions = show };
    /// <summary>Returns a copy with base-path exclusion set to <paramref name="exclude"/>.</summary>
    /// <param name="exclude"><c>true</c> removes the namespace root node from results.</param>
    public MeshSearchControl WithExcludeBasePath(bool exclude) => this with { ExcludeBasePath = exclude };
    /// <summary>Returns a copy with live-search set to <paramref name="live"/>.</summary>
    /// <param name="live"><c>false</c> restricts search to trigger only on Enter.</param>
    public MeshSearchControl WithLiveSearch(bool live) => this with { LiveSearch = live };

    // Grouping fluent methods
    /// <summary>Returns a copy with results grouped by <paramref name="property"/>.</summary>
    /// <param name="property">The property name (camelCase) to group results by.</param>
    public MeshSearchControl WithGroupBy(string property) =>
        this with { Grouping = (Grouping ?? new GroupingConfig()) with { GroupByProperty = property } };

    // Section fluent methods
    /// <summary>Returns a copy with section item-count display set to <paramref name="showCounts"/>.</summary>
    /// <param name="showCounts"><c>true</c> shows the count of items in each section heading.</param>
    public MeshSearchControl WithSectionCounts(bool showCounts) =>
        this with { Sections = (Sections ?? new SectionConfig()) with { ShowCounts = showCounts } };

    /// <summary>Returns a copy with the per-section item limit set to <paramref name="limit"/>.</summary>
    /// <param name="limit">Maximum items to show per section before truncation.</param>
    public MeshSearchControl WithItemLimit(int limit) =>
        this with { Sections = (Sections ?? new SectionConfig()) with { ItemLimit = limit } };

    /// <summary>Returns a copy with the maximum visible rows per section set to <paramref name="rows"/>.</summary>
    /// <param name="rows">Maximum number of rows displayed per section.</param>
    public MeshSearchControl WithMaxRows(int rows) =>
        this with { Sections = (Sections ?? new SectionConfig()) with { MaxRows = rows } };

    /// <summary>Returns a copy with section collapsibility set to <paramref name="collapsible"/>.</summary>
    /// <param name="collapsible"><c>true</c> renders sections as collapsible accordion panels.</param>
    public MeshSearchControl WithCollapsibleSections(bool collapsible) =>
        this with { Sections = (Sections ?? new SectionConfig()) with { Collapsible = collapsible } };

    /// <summary>Returns a copy with a "Show all" link pointing to <paramref name="href"/> appended to each section.</summary>
    /// <param name="href">The URL for the "Show all" link rendered below a truncated section.</param>
    public MeshSearchControl WithShowAllHref(string href) =>
        this with { Sections = (Sections ?? new SectionConfig()) with { ShowAllHref = href } };

    // Sorting fluent methods
    /// <summary>Returns a copy sorted by <paramref name="property"/>.</summary>
    /// <param name="property">The property name (camelCase) to sort by.</param>
    /// <param name="ascending"><c>true</c> for ascending order; <c>false</c> for descending.</param>
    public MeshSearchControl WithSortBy(string property, bool ascending = true) =>
        this with { Sorting = (Sorting ?? new SortConfig()) with { SortByProperty = property, Ascending = ascending } };

    /// <summary>Returns a copy with a secondary sort by <paramref name="property"/> applied after the primary sort.</summary>
    /// <param name="property">The property name (camelCase) for the secondary sort.</param>
    /// <param name="ascending"><c>true</c> for ascending; <c>false</c> for descending.</param>
    public MeshSearchControl WithThenBy(string property, bool ascending = true) =>
        this with { Sorting = (Sorting ?? new SortConfig()) with { ThenByProperty = property, ThenByAscending = ascending } };

    // Grid fluent methods
    /// <summary>Returns a copy with responsive grid column widths set per breakpoint (MUI grid units, 1–12).</summary>
    /// <param name="xs">Column width on extra-small screens (default 12).</param>
    /// <param name="sm">Column width on small screens (default 6).</param>
    /// <param name="md">Column width on medium screens (default 4).</param>
    /// <param name="lg">Column width on large screens (default 4).</param>
    public MeshSearchControl WithGridBreakpoints(int xs = 12, int sm = 6, int md = 4, int lg = 4) =>
        this with { Grid = new GridConfig { Xs = xs, Sm = sm, Md = md, Lg = lg, Spacing = Grid?.Spacing ?? 2 } };

    /// <summary>Returns a copy with the grid spacing set to <paramref name="spacing"/> (MUI spacing units).</summary>
    /// <param name="spacing">Spacing between grid items; default 2.</param>
    public MeshSearchControl WithGridSpacing(int spacing) =>
        this with { Grid = (Grid ?? new GridConfig()) with { Spacing = spacing } };

    // Show empty message
    /// <summary>Returns a copy with empty-results message display set to <paramref name="show"/>.</summary>
    /// <param name="show"><c>false</c> renders nothing when search returns no items.</param>
    public MeshSearchControl WithShowEmptyMessage(bool show) => this with { ShowEmptyMessage = show };

    // Show loading indicator
    /// <summary>Returns a copy with the skeleton-card loading indicator set to <paramref name="show"/>.</summary>
    /// <param name="show"><c>false</c> suppresses skeleton cards while results load.</param>
    public MeshSearchControl WithShowLoadingIndicator(bool show) => this with { ShowLoadingIndicator = show };

    // Reactive mode
    /// <summary>Returns a copy with reactive live-update mode set to <paramref name="reactive"/>.</summary>
    /// <param name="reactive"><c>true</c> causes results to update automatically when underlying data changes.</param>
    public MeshSearchControl WithReactiveMode(bool reactive) => this with { ReactiveMode = reactive };

    // Item area (render each item via LayoutAreaView)
    /// <summary>Returns a copy where each result card is rendered via a LayoutAreaView pointing to <paramref name="area"/>.</summary>
    /// <param name="area">The area name used to render each result item (e.g. <c>"Thumbnail"</c>).</param>
    public MeshSearchControl WithItemArea(string area) => this with { ItemArea = area };

    // Disable navigation on card click
    /// <summary>Returns a copy with card-click navigation disabled or enabled.</summary>
    /// <param name="disable"><c>true</c> suppresses browser navigation when a card is clicked.</param>
    public MeshSearchControl WithDisableNavigation(bool disable = true) => this with { DisableNavigation = disable };

    // Pre-computed groups
    /// <summary>Returns a copy with pre-computed grouped results that bypass client-side grouping logic.</summary>
    /// <param name="groups">The serializable output of <c>ProcessResults()</c> to use directly.</param>
    public MeshSearchControl WithPrecomputedGroups(GroupedSearchResult groups) => this with { PrecomputedGroups = groups };

    // Create node
    /// <summary>Returns a copy that shows a "+" button creating a transient node of type <paramref name="nodeType"/>.</summary>
    /// <param name="nodeType">The node type identifier for newly created nodes.</param>
    public MeshSearchControl WithCreateNodeType(string nodeType) => this with { CreateNodeType = nodeType };
    /// <summary>Returns a copy with the namespace for new node creation set to <paramref name="ns"/>.</summary>
    /// <param name="ns">The namespace where new nodes are created; defaults to the HiddenQuery namespace.</param>
    public MeshSearchControl WithCreateNamespace(string ns) => this with { CreateNamespace = ns };
    /// <summary>Returns a copy that shows a "+" button navigating directly to <paramref name="href"/> (takes priority over CreateNodeType).</summary>
    /// <param name="href">The URL the "+" button navigates to.</param>
    public MeshSearchControl WithCreateHref(string href) => this with { CreateHref = href };

    // Drill-down: secondary "keep browsing into this namespace" affordance.
    /// <summary>Returns a copy with a secondary drill-down affordance pointing to the <paramref name="area"/> sub-area of each result.</summary>
    /// <param name="area">The area name appended to <c>/{path}/{area}</c> for the drill-down link.</param>
    public MeshSearchControl WithDrillDownArea(string area) => this with { DrillDownArea = area };
}
