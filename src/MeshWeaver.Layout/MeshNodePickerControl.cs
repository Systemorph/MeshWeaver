using MeshWeaver.Domain;

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

    /// <summary>Returns a copy with an empty-value option added at the top of the dropdown.</summary>
    /// <param name="label">Label for the empty option (e.g. "Root (top-level)").</param>
    public MeshNodePickerControl WithEmptyOption(string label = "Root (top-level)")
        => this with { EmptyOptionLabel = label };

    /// <summary>Returns a copy with <paramref name="queries"/> as the search queries run in parallel.</summary>
    /// <param name="queries">One or more mesh query strings; the user's typed text is appended to each.</param>
    public MeshNodePickerControl WithQueries(params string[] queries)
        => this with { Queries = queries };

    /// <summary>Returns a copy scoped to <paramref name="ns"/> as the search namespace.</summary>
    /// <param name="ns">The namespace to restrict results to, or a binding expression.</param>
    public MeshNodePickerControl WithNamespace(object ns)
        => this with { Namespace = ns };

    /// <summary>Returns a copy with <paramref name="maxResults"/> as the maximum number of dropdown items shown.</summary>
    /// <param name="maxResults">Maximum result count or binding expression.</param>
    public MeshNodePickerControl WithMaxResults(object maxResults)
        => this with { MaxResults = maxResults };

    /// <summary>
    /// Returns a copy with a fixed set of items merged into the search results.
    /// Useful for small, known sets (e.g. roles, creatable types).
    /// </summary>
    /// <param name="items">MeshNode instances to include alongside query results.</param>
    public MeshNodePickerControl WithItems(params object[] items)
        => this with { Items = items };

    /// <summary>
    /// Visual density of the picker (full card vs. compact). Defaults to
    /// <see cref="MeshNodePickerLayout.Default"/>.
    /// </summary>
    public MeshNodePickerLayout Layout { get; init; }

    /// <summary>
    /// Direction the dropdown opens (down by default; up for bottom-anchored fields
    /// such as the chat composer).
    /// </summary>
    public MeshNodePickerOpenDirection Open { get; init; }

    /// <summary>
    /// When true and no value is set, the picker auto-selects (and persists) the first
    /// available result as the default.
    /// </summary>
    public bool DefaultToFirst { get; init; }

    /// <summary>
    /// When true, the queries load ONCE without the typed text and the view filters the
    /// cached result set in-memory (diacritic- and case-insensitive — "Burgi" finds "Bürgi").
    /// Use for bounded result sets (users, groups, roles) where server-side substring search
    /// would miss accented matches; leave false for large/open sets that need server search.
    /// </summary>
    public bool FilterInMemory { get; init; }

    /// <summary>Returns a copy with <paramref name="layout"/> controlling the visual density of picker results.</summary>
    /// <param name="layout">The desired layout variant (e.g. compact or full card).</param>
    public MeshNodePickerControl WithLayout(MeshNodePickerLayout layout)
        => this with { Layout = layout };

    /// <summary>Returns a copy with <paramref name="open"/> controlling the direction the dropdown opens.</summary>
    /// <param name="open">The open direction (e.g. up for bottom-anchored fields).</param>
    public MeshNodePickerControl WithOpenDirection(MeshNodePickerOpenDirection open)
        => this with { Open = open };

    /// <summary>Returns a copy with <paramref name="defaultToFirst"/> controlling whether the first available result is auto-selected when no value is set.</summary>
    /// <param name="defaultToFirst">When <c>true</c>, the first result is selected and persisted automatically.</param>
    public MeshNodePickerControl WithDefaultToFirst(bool defaultToFirst = true)
        => this with { DefaultToFirst = defaultToFirst };
}
