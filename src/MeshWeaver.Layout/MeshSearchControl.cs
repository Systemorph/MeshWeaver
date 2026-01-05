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

    public MeshSearchControl WithHiddenQuery(string query) => this with { HiddenQuery = query };
    public MeshSearchControl WithVisibleQuery(string query) => this with { VisibleQuery = query };
    public MeshSearchControl WithPlaceholder(string placeholder) => this with { Placeholder = placeholder };
    public MeshSearchControl WithNamespace(string ns) => this with { Namespace = ns };
    public MeshSearchControl WithRenderMode(MeshSearchRenderMode mode) => this with { RenderMode = mode };
    public MeshSearchControl WithMaxColumns(int columns) => this with { MaxColumns = columns };
    public MeshSearchControl WithShowSearchBox(bool show) => this with { ShowSearchBox = show };
    public MeshSearchControl WithExcludeBasePath(bool exclude) => this with { ExcludeBasePath = exclude };
    public MeshSearchControl WithLiveSearch(bool live) => this with { LiveSearch = live };
}
