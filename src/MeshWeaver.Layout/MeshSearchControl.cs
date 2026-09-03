using System.Collections.Generic;
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
    GraphNavigator,

    /// <summary>
    /// A phone-home ICON grid: each result renders as a large rounded icon with its name
    /// underneath — the home's Apps look. Rendered entirely from the query row (name/icon are
    /// result columns), so no per-result content read or hub activation happens. Appended last:
    /// enum members serialize by NAME, but the ordinal must stay stable for older rows.
    /// </summary>
    Icons
}

/// <summary>
/// One user-selectable sort choice for the view-options "Sort by" dropdown: a display
/// <paramref name="Label"/> and the full hidden <paramref name="Query"/> applied when the user picks
/// it. Each option is a complete query so an option can change BOTH the ordering AND the result set
/// (e.g. "Last accessed" uses <c>source:accessed</c> — the user's accessed working set, ordered by
/// access recency — while "Last modified" / "Alphabetical" span the full readable set). The FIRST
/// option is the default and should match the control's <see cref="MeshSearchControl.HiddenQuery"/>.
/// </summary>
/// <param name="Label">The label shown in the "Sort by" dropdown.</param>
/// <param name="Query">The full hidden query applied when this option is selected.</param>
public record MeshSearchSortOption(string Label, string Query);

/// <summary>
/// One SCOPE tab of a search surface — a display <paramref name="Label"/> and the scope's hidden
/// <paramref name="Query"/>, rendered as a tab strip above the search header. Switching scopes
/// swaps only the hidden query (and, when <see cref="SortOptions"/> is set, the sort choices)
/// while the typed search text and the rest of the component state STAY — the scopes deliberately
/// SHARE one search bar, which is what lets a search term follow the user across tabs. The FIRST
/// tab is the initially active scope and should match the control's
/// <see cref="MeshSearchControl.HiddenQuery"/>, which also serves as the fallback for clients
/// that don't render scopes.
/// </summary>
/// <param name="Label">The tab's display text.</param>
/// <param name="Query">The scope's full hidden query.</param>
public record MeshSearchScopeTab(string Label, string Query)
{
    /// <summary>
    /// Sort choices that REPLACE the control-level <see cref="MeshSearchControl.SortOptions"/>
    /// while this scope is active (first = this scope's default). Null keeps the control-level set.
    /// </summary>
    public IReadOnlyList<MeshSearchSortOption>? SortOptions { get; init; }

    /// <summary>
    /// Per-item layout area used while this scope is active, REPLACING the control-level
    /// <see cref="MeshSearchControl.ItemArea"/>. Null keeps the control-level one. Prefer a
    /// row-rendered mode (e.g. <see cref="MeshSearchRenderMode.Icons"/>) over an item area where
    /// the row data suffices — an item area activates one hub PER RESULT.
    /// </summary>
    public string? ItemArea { get; init; }

    /// <summary>
    /// Render mode while this scope is active, REPLACING the control-level
    /// <see cref="MeshSearchControl.RenderMode"/> (the enum member's NAME, e.g. <c>"Icons"</c> —
    /// the home's Apps scope renders the phone-home icon grid this way). Null keeps the
    /// control-level mode.
    /// </summary>
    public string? RenderMode { get; init; }

    /// <summary>
    /// When true, clicking a result of this scope navigates to the node's <c>MainNode</c> instead
    /// of its own path — the home's Apps records point at the APP they represent this way, with no
    /// content read. Null keeps the control-level <see cref="MeshSearchControl.NavigateToMainNode"/>.
    /// </summary>
    public bool? NavigateToMainNode { get; init; }

    /// <summary>
    /// When true, the Icons grid of this scope is REARRANGEABLE by drag and drop: a tile's
    /// <c>content.order</c> is its position inside its group and <c>content.group</c> (the
    /// control's group-by property) the section it sits in; dropping a tile rewrites both on the
    /// records themselves through the node stream — per viewer, nowhere else. Null keeps the
    /// control-level <see cref="MeshSearchControl.Sortable"/>.
    /// </summary>
    public bool? Sortable { get; init; }

    /// <summary>
    /// Order this scope's results by when the VIEWER last opened each result's navigation target,
    /// most recent first, with never-opened results keeping the query's own order behind them —
    /// the phone-home rule: what you use most sits where your thumb is. Applied wherever results
    /// are projected, so every render mode honours it, not just the icon grid.
    /// <para>Applied at PAINT, not in the query, and deliberately: <c>source:accessed</c> is an
    /// INNER JOIN on the access log keyed by the result's OWN path, so on the Apps grid it would
    /// both hide every never-opened app AND match nothing (an app record's access is recorded
    /// against the app it points at, never against the record). The view instead reads the
    /// viewer's own <c>_UserActivity</c> satellites — one cheap single-partition query — and uses
    /// them as a SORT KEY. Ordering arrives with that snapshot, after the tiles have painted.</para>
    /// </summary>
    public bool SortByAccess { get; init; }
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
    /// Minimum width of one result cell in px (default 200). The card grid never shrinks a cell
    /// below this, so lowering it is what makes a COMPACT, many-per-row tile band possible — the
    /// home's Apps dock uses it to fit its icons on one row instead of four giant cards. Clients
    /// that don't know the property keep the default cell size.
    /// </summary>
    public object? MinItemWidth { get; init; }

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
    /// Optional user-selectable sort choices for the view-options "Sort by" dropdown (only rendered
    /// when <see cref="ShowViewOptions"/> is on). Each entry carries a full hidden query, so picking
    /// one can change the ordering AND the result set. The FIRST entry is the default and should equal
    /// <see cref="HiddenQuery"/>. Null/empty ⇒ no sort dropdown (unchanged behaviour).
    /// </summary>
    public IReadOnlyList<MeshSearchSortOption>? SortOptions { get; init; }

    /// <summary>
    /// Scope tabs rendered as a strip above the search header (see
    /// <see cref="MeshSearchScopeTab"/>): switching swaps the hidden query and (optionally) the
    /// sort choices while the typed search text stays — the scopes SHARE one search bar. Null or
    /// a single entry renders no strip. Clients without scope support fall back to
    /// <see cref="HiddenQuery"/>, which should equal the first tab's query.
    /// </summary>
    public IReadOnlyList<MeshSearchScopeTab>? ScopeTabs { get; init; }

    /// <summary>
    /// In a grouped render, order the sections by SIZE (most items first) instead of
    /// alphabetically — the home's content section fans out by node type with the type you have
    /// most of at the top, so the page opens on what you actually work with rather than on
    /// whatever happens to start with "A". Ties fall back to the label, so the order is stable.
    /// </summary>
    public object? GroupByFrequency { get; init; }

    /// <summary>Sets <see cref="GroupByFrequency"/>.</summary>
    public MeshSearchControl WithGroupByFrequency(bool value = true) =>
        This with { GroupByFrequency = value };

    /// <summary>
    /// When true, clicking a result navigates to the node's <c>MainNode</c> instead of its own
    /// path (default false). A per-scope <see cref="MeshSearchScopeTab.NavigateToMainNode"/>
    /// overrides this while its scope is active.
    /// </summary>
    public object? NavigateToMainNode { get; init; }

    /// <summary>
    /// When true, the Icons grid can be REARRANGED by drag and drop (default false): tiles are
    /// ordered by their <c>content.order</c> inside the sections the group-by property forms, and
    /// a drop writes the moved tiles' <c>order</c> (and <c>group</c>) back onto the result nodes
    /// through the node stream. Tiles without an order trail the ordered ones in the grid's own
    /// paint order. A per-scope <see cref="MeshSearchScopeTab.Sortable"/> overrides this.
    /// </summary>
    public object? Sortable { get; init; }

    /// <summary>Sets <see cref="Sortable"/>.</summary>
    public MeshSearchControl WithSortable(bool value = true) => This with { Sortable = value };

    /// <summary>
    /// Whether to exclude the base path node from results (default true).
    /// When true, the node at the namespace path itself is not shown.
    /// </summary>
    public object? ExcludeBasePath { get; init; }

    /// <summary>
    /// Whether nodes under an underscore-prefixed segment RELATIVE to <see cref="Namespace"/>
    /// (<c>_Entitlements</c>, <c>_Access</c>, <c>_Policy</c>, …) are shown. Default FALSE — those
    /// satellites are governance bookkeeping, not content, and a node's Contents catalog listing
    /// them reads as clutter at best and as leaked internals at worst. The filter is
    /// namespace-RELATIVE and only active when <see cref="Namespace"/> anchors the control, so an
    /// unanchored search (the global /search page, the top-bar Thread/Agent searches — whose
    /// results legitimately LIVE under satellites) is untouched.
    /// </summary>
    public object? IncludeHidden { get; init; }

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

    /// <summary>Sets the minimum width of one result cell in px (see <see cref="MinItemWidth"/>).</summary>
    public MeshSearchControl WithMinItemWidth(int px) => this with { MinItemWidth = px };
    /// <summary>Returns a copy with the search-box visibility set to <paramref name="show"/>.</summary>
    /// <param name="show"><c>false</c> hides the search box, showing only results.</param>
    public MeshSearchControl WithShowSearchBox(bool show) => this with { ShowSearchBox = show };
    /// <summary>Returns a copy with the view-options bar enabled or disabled.</summary>
    /// <param name="show"><c>true</c> renders the Group-by combobox and display-menu above results.</param>
    public MeshSearchControl WithViewOptions(bool show = true) => this with { ShowViewOptions = show };
    /// <summary>Returns a copy with base-path exclusion set to <paramref name="exclude"/>.</summary>
    /// <param name="exclude"><c>true</c> removes the namespace root node from results.</param>
    public MeshSearchControl WithExcludeBasePath(bool exclude) => this with { ExcludeBasePath = exclude };

    /// <summary>Show nodes under underscore-prefixed satellite segments (see <see cref="IncludeHidden"/>).</summary>
    public MeshSearchControl WithIncludeHidden(bool include) => this with { IncludeHidden = include };
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

    /// <summary>Returns a copy with the view-options "Sort by" dropdown offering <paramref name="options"/>.</summary>
    /// <param name="options">The user-selectable sort choices; the first is the default and should match <see cref="HiddenQuery"/>.</param>
    public MeshSearchControl WithSortOptions(params MeshSearchSortOption[] options) =>
        this with { SortOptions = options };

    /// <summary>Returns a copy with the scope-tab strip set (see <see cref="ScopeTabs"/>).</summary>
    /// <param name="tabs">The scopes; the first is initially active and should match <see cref="HiddenQuery"/>.</param>
    public MeshSearchControl WithScopeTabs(params MeshSearchScopeTab[] tabs) =>
        this with { ScopeTabs = tabs };

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
