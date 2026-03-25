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
    /// Hierarchical display - tree structure with parent-child indentation.
    /// Each root node and its subtree kept in a single grid cell.
    /// </summary>
    Hierarchical,

    /// <summary>
    /// Grouped by category - results grouped under category headings.
    /// </summary>
    Grouped
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
    /// Whether to use ObserveQuery for reactive updates (default false).
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

    // Basic fluent methods
    public MeshSearchControl WithTitle(string title) => this with { Title = title };
    public MeshSearchControl WithClickMessageAddress(object address) => this with { ClickMessageAddress = address };
    public MeshSearchControl WithHiddenQuery(string query) => this with { HiddenQuery = query };
    public MeshSearchControl WithVisibleQuery(string query) => this with { VisibleQuery = query };
    public MeshSearchControl WithPlaceholder(string placeholder) => this with { Placeholder = placeholder };
    public MeshSearchControl WithNamespace(string ns) => this with { Namespace = ns };
    public MeshSearchControl WithRenderMode(MeshSearchRenderMode mode) => this with { RenderMode = mode };
    public MeshSearchControl WithMaxColumns(int columns) => this with { MaxColumns = columns };
    public MeshSearchControl WithShowSearchBox(bool show) => this with { ShowSearchBox = show };
    public MeshSearchControl WithExcludeBasePath(bool exclude) => this with { ExcludeBasePath = exclude };
    public MeshSearchControl WithLiveSearch(bool live) => this with { LiveSearch = live };

    // Grouping fluent methods
    public MeshSearchControl WithGroupBy(string property) =>
        this with { Grouping = (Grouping ?? new GroupingConfig()) with { GroupByProperty = property } };

    // Section fluent methods
    public MeshSearchControl WithSectionCounts(bool showCounts) =>
        this with { Sections = (Sections ?? new SectionConfig()) with { ShowCounts = showCounts } };

    public MeshSearchControl WithItemLimit(int limit) =>
        this with { Sections = (Sections ?? new SectionConfig()) with { ItemLimit = limit } };

    public MeshSearchControl WithCollapsibleSections(bool collapsible) =>
        this with { Sections = (Sections ?? new SectionConfig()) with { Collapsible = collapsible } };

    public MeshSearchControl WithShowAllHref(string href) =>
        this with { Sections = (Sections ?? new SectionConfig()) with { ShowAllHref = href } };

    // Sorting fluent methods
    public MeshSearchControl WithSortBy(string property, bool ascending = true) =>
        this with { Sorting = (Sorting ?? new SortConfig()) with { SortByProperty = property, Ascending = ascending } };

    public MeshSearchControl WithThenBy(string property, bool ascending = true) =>
        this with { Sorting = (Sorting ?? new SortConfig()) with { ThenByProperty = property, ThenByAscending = ascending } };

    // Grid fluent methods
    public MeshSearchControl WithGridBreakpoints(int xs = 12, int sm = 6, int md = 4, int lg = 4) =>
        this with { Grid = new GridConfig { Xs = xs, Sm = sm, Md = md, Lg = lg, Spacing = Grid?.Spacing ?? 2 } };

    public MeshSearchControl WithGridSpacing(int spacing) =>
        this with { Grid = (Grid ?? new GridConfig()) with { Spacing = spacing } };

    // Show empty message
    public MeshSearchControl WithShowEmptyMessage(bool show) => this with { ShowEmptyMessage = show };

    // Show loading indicator
    public MeshSearchControl WithShowLoadingIndicator(bool show) => this with { ShowLoadingIndicator = show };

    // Reactive mode
    public MeshSearchControl WithReactiveMode(bool reactive) => this with { ReactiveMode = reactive };

    // Item area (render each item via LayoutAreaView)
    public MeshSearchControl WithItemArea(string area) => this with { ItemArea = area };

    // Disable navigation on card click
    public MeshSearchControl WithDisableNavigation(bool disable = true) => this with { DisableNavigation = disable };

    // Pre-computed groups
    public MeshSearchControl WithPrecomputedGroups(GroupedSearchResult groups) => this with { PrecomputedGroups = groups };

    // Create node
    public MeshSearchControl WithCreateNodeType(string nodeType) => this with { CreateNodeType = nodeType };
    public MeshSearchControl WithCreateNamespace(string ns) => this with { CreateNamespace = ns };
    public MeshSearchControl WithCreateHref(string href) => this with { CreateHref = href };
}
