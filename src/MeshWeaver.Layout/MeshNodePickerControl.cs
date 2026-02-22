namespace MeshWeaver.Layout;

/// <summary>
/// A form control that provides a searchable picker for mesh nodes.
/// Uses MeshSearchView internally to display search results as cards.
/// The selected node's Path is stored as the form value.
/// </summary>
public record MeshNodePickerControl(object Data)
    : FormControlBase<MeshNodePickerControl>(Data)
{
    /// <summary>
    /// A query string that is always applied but not visible to the user.
    /// E.g. "nodeType:(User OR Group) scope:selfAndAncestors"
    /// </summary>
    public object? HiddenQuery { get; init; }

    /// <summary>
    /// The namespace to scope the search to.
    /// </summary>
    public object? Namespace { get; init; }

    /// <summary>
    /// Maximum number of results to show in the dropdown.
    /// </summary>
    public object? MaxResults { get; init; }

    public MeshNodePickerControl WithHiddenQuery(object hiddenQuery)
        => this with { HiddenQuery = hiddenQuery };

    public MeshNodePickerControl WithNamespace(object ns)
        => this with { Namespace = ns };

    public MeshNodePickerControl WithMaxResults(object maxResults)
        => this with { MaxResults = maxResults };
}
