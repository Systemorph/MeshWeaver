namespace MeshWeaver.Blazor.Portal.Chat;

/// <summary>
/// Options for configuring MeshNodeAutocomplete behavior.
/// </summary>
public class MeshNodeAutocompleteOptions
{
    /// <summary>
    /// Base path to search from. Empty string searches from root.
    /// </summary>
    public string BasePath { get; set; } = "";

    /// <summary>
    /// Maximum number of suggestions to return.
    /// </summary>
    public int Limit { get; set; } = 15;

    /// <summary>
    /// When set, only return nodes that can create this node type.
    /// This filters suggestions to nodes where GetCreatableTypesAsync includes this type.
    /// </summary>
    public string? FilterByCreatableType { get; set; }

    /// <summary>
    /// Placeholder text for the search input.
    /// </summary>
    public string Placeholder { get; set; } = "Select namespace...";

    /// <summary>
    /// Placeholder text for the search input field.
    /// </summary>
    public string SearchPlaceholder { get; set; } = "Type to search...";

    /// <summary>
    /// Width of the dropdown panel.
    /// </summary>
    public string PanelWidth { get; set; } = "100%";

    /// <summary>
    /// Whether to show the search input in the dropdown.
    /// </summary>
    public bool ShowSearch { get; set; } = true;

    /// <summary>
    /// CSS class to apply to the component.
    /// </summary>
    public string? CssClass { get; set; }
}
