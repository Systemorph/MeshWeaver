namespace MeshWeaver.Layout;

/// <summary>
/// A form control that provides a searchable picker for mesh nodes.
/// Uses MeshSearchView internally to display search results as cards.
/// The selected node's Path is stored as the form value.
/// Supports multiple queries that are run in parallel and merged.
/// </summary>
public record MeshNodePickerControl(object Data)
    : FormControlBase<MeshNodePickerControl>(Data)
{
    /// <summary>
    /// Multiple query strings run in parallel and merged.
    /// E.g. ["namespace:User nodeType:User", "path:X nodeType:Group scope:selfAndAncestors"]
    /// The user's typed text is appended to each query.
    /// </summary>
    public string[]? Queries { get; init; }

    /// <summary>
    /// The namespace to scope the search to.
    /// </summary>
    public object? Namespace { get; init; }

    /// <summary>
    /// Maximum number of results to show in the dropdown.
    /// </summary>
    public object? MaxResults { get; init; }

    /// <summary>
    /// Fixed set of items (MeshNode instances) to include in the picker.
    /// These are merged with query results (deduplicated by Path)
    /// and the combined set is cached for in-memory filtering.
    /// Use for small, known sets (e.g., creatable types, roles).
    /// Typed as object[] due to circular dependency constraints;
    /// MeshNodePickerView casts to MeshNode[].
    /// </summary>
    public object[]? Items { get; init; }

    /// <summary>
    /// When set, adds an option at the top of the dropdown that selects an empty value ("").
    /// E.g., "Root (top-level)" for namespace pickers.
    /// </summary>
    public string? EmptyOptionLabel { get; init; }

    public MeshNodePickerControl WithEmptyOption(string label = "Root (top-level)")
        => this with { EmptyOptionLabel = label };

    public MeshNodePickerControl WithQueries(params string[] queries)
        => this with { Queries = queries };

    public MeshNodePickerControl WithNamespace(object ns)
        => this with { Namespace = ns };

    public MeshNodePickerControl WithMaxResults(object maxResults)
        => this with { MaxResults = maxResults };

    public MeshNodePickerControl WithItems(params object[] items)
        => this with { Items = items };
}
