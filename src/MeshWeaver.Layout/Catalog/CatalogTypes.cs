namespace MeshWeaver.Layout.Catalog;

/// <summary>
/// Configuration for grouping search/catalog results by a property.
/// Supports dynamic property-based grouping.
/// Note: Lambda-based formatting/ordering is now handled via extension methods on IMessageHub
/// (see ObserveMeshSearch/ObserveMeshSearchByProperty in MeshSearchExtensions).
/// </summary>
public record GroupingConfig
{
    /// <summary>
    /// The property to group by (e.g., "Category", "Status", "NodeType").
    /// Can be a MeshNode property or a Content property.
    /// </summary>
    public string? GroupByProperty { get; init; }
}

// FilterConfig removed - filtering is now handled via lambda predicates
// passed to extension methods (see ObserveMeshSearch in MeshSearchExtensions).

/// <summary>
/// Configuration for section display including counts, limits, and collapsibility.
/// </summary>
public record SectionConfig
{
    /// <summary>
    /// Whether to show item counts in section headers (default true).
    /// </summary>
    public bool ShowCounts { get; init; } = true;

    /// <summary>
    /// Maximum number of items to show per section before showing "Show more".
    /// Null means no limit.
    /// </summary>
    public int? ItemLimit { get; init; }

    /// <summary>
    /// Whether sections can be collapsed/expanded (default true).
    /// </summary>
    public bool Collapsible { get; init; } = true;

    /// <summary>
    /// When set, a "Show all" link is rendered next to "Showing X of Y"
    /// that navigates to this URL (typically a full search page with the query pre-populated).
    /// </summary>
    public string? ShowAllHref { get; init; }
}

/// <summary>
/// Configuration for sorting search/catalog results.
/// Supports primary and secondary sort properties.
/// </summary>
public record SortConfig
{
    /// <summary>
    /// The property to sort by (e.g., "LastModified", "Name", "DueDate").
    /// Can be a MeshNode property or a Content property.
    /// </summary>
    public string? SortByProperty { get; init; }

    /// <summary>
    /// Whether to sort in ascending order (default true).
    /// </summary>
    public bool Ascending { get; init; } = true;

    /// <summary>
    /// Optional secondary sort property for tie-breaking.
    /// </summary>
    public string? ThenByProperty { get; init; }

    /// <summary>
    /// Whether secondary sort is ascending (default true).
    /// </summary>
    public bool ThenByAscending { get; init; } = true;
}

/// <summary>
/// Configuration for responsive grid layout breakpoints.
/// </summary>
public record GridConfig
{
    /// <summary>
    /// Column span for extra-small screens (default 12 = full width).
    /// </summary>
    public int Xs { get; init; } = 12;

    /// <summary>
    /// Column span for small screens (default 6 = 2 columns).
    /// </summary>
    public int Sm { get; init; } = 6;

    /// <summary>
    /// Column span for medium screens (default 4 = 3 columns).
    /// </summary>
    public int Md { get; init; } = 4;

    /// <summary>
    /// Column span for large screens (default 4 = 3 columns).
    /// </summary>
    public int Lg { get; init; } = 4;

    /// <summary>
    /// Grid spacing between items (default 3).
    /// </summary>
    public int Spacing { get; init; } = 3;
}
