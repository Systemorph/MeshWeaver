#nullable enable

namespace MeshWeaver.AI.Completion;

/// <summary>
/// Kind of autocomplete item (determines the icon shown).
/// </summary>
public enum AutocompleteKind
{
    /// <summary>AI Agent</summary>
    Agent,
    /// <summary>File reference</summary>
    File,
    /// <summary>Command or action</summary>
    Command,
    /// <summary>Other item</summary>
    Other
}

/// <summary>
/// Represents an item in the autocomplete dropdown.
/// </summary>
/// <param name="Label">Display text shown in the autocomplete dropdown.</param>
/// <param name="InsertText">Text that gets inserted when the item is selected.</param>
/// <param name="Description">Additional description shown in the dropdown.</param>
/// <param name="Category">Category for grouping (e.g., "Agents", "Files"). Agents have priority over Files.</param>
/// <param name="Priority">Priority within category. Higher values are shown first.</param>
/// <param name="Kind">The kind of item (Agent, File, Command) - determines icon.</param>
public record AutocompleteItem(
    string Label,
    string InsertText,
    string? Description = null,
    string Category = "Files",
    int Priority = 0,
    AutocompleteKind Kind = AutocompleteKind.Other
);
