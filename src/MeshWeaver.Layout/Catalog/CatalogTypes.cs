using System.Text.Json.Serialization;

namespace MeshWeaver.Layout.Catalog;

/// <summary>
/// Configuration for grouping search/catalog results by a property.
/// Supports dynamic property-based grouping with custom formatting and ordering.
/// </summary>
public record GroupingConfig
{
    /// <summary>
    /// The property to group by (e.g., "Category", "Status", "NodeType").
    /// Can be a MeshNode property or a Content property.
    /// </summary>
    public string? GroupByProperty { get; init; }

    /// <summary>
    /// Optional formatter to transform group keys into display labels.
    /// Type: Func&lt;string?, string&gt;
    /// Server-side only - not serialized.
    /// </summary>
    [JsonIgnore]
    public object? GroupKeyFormatter { get; init; }

    /// <summary>
    /// Optional function to determine sort order of groups.
    /// Type: Func&lt;string?, int&gt;
    /// Server-side only - not serialized.
    /// </summary>
    [JsonIgnore]
    public object? GroupKeyOrder { get; init; }

    /// <summary>
    /// Optional predicate to determine which groups are expanded by default.
    /// Type: Func&lt;string?, bool&gt;
    /// Server-side only - not serialized.
    /// </summary>
    [JsonIgnore]
    public object? GroupExpandedPredicate { get; init; }
}

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
    /// Optional function to build the "Show more" href for a group.
    /// Type: Func&lt;string, string&gt;
    /// Server-side only - not serialized.
    /// </summary>
    [JsonIgnore]
    public object? ShowMoreHrefBuilder { get; init; }
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
    /// Grid spacing between items (default 2).
    /// </summary>
    public int Spacing { get; init; } = 2;
}
