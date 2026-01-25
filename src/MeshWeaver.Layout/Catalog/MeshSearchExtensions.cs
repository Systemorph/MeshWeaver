namespace MeshWeaver.Layout.Catalog;

/// <summary>
/// Represents a group of search results with pre-computed label and metadata.
/// This structure is fully serializable.
/// </summary>
public record SearchResultGroup
{
    /// <summary>
    /// The raw group key (e.g., "Category1", "Pending").
    /// </summary>
    public string GroupKey { get; init; } = "";

    /// <summary>
    /// The formatted display label for this group.
    /// </summary>
    public string Label { get; init; } = "";

    /// <summary>
    /// Sort order for this group (lower = first).
    /// </summary>
    public int Order { get; init; }

    /// <summary>
    /// Whether this group should be expanded by default.
    /// </summary>
    public bool IsExpanded { get; init; } = true;

    /// <summary>
    /// The items in this group (already sorted).
    /// Stored as objects to avoid dependency on MeshNode.
    /// Cast to appropriate type when rendering.
    /// </summary>
    public IReadOnlyList<object> Items { get; init; } = [];

    /// <summary>
    /// Total count of items (may differ from Items.Count if limited).
    /// </summary>
    public int TotalCount { get; init; }
}

/// <summary>
/// Pre-computed grouping result that can be serialized.
/// </summary>
public record GroupedSearchResult
{
    /// <summary>
    /// The groups in display order.
    /// </summary>
    public IReadOnlyList<SearchResultGroup> Groups { get; init; } = [];

    /// <summary>
    /// Total number of items across all groups.
    /// </summary>
    public int TotalItems { get; init; }
}
