#nullable enable

namespace MeshWeaver.AI.Completion;

/// <summary>
/// Represents an item in the autocomplete dropdown.
/// </summary>
/// <param name="Label">Display text shown in the autocomplete dropdown.</param>
/// <param name="InsertText">Text that gets inserted when the item is selected.</param>
/// <param name="Description">Additional description shown in the dropdown.</param>
/// <param name="Category">Category for grouping (e.g., "Agents", "Files"). Agents have priority over Files.</param>
/// <param name="Priority">Priority within category. Higher values are shown first.</param>
public record AutocompleteItem(
    string Label,
    string InsertText,
    string? Description = null,
    string Category = "Files",
    int Priority = 0
);
