#nullable enable

namespace MeshWeaver.Data.Completion;

/// <summary>
/// Types of autocomplete items for display purposes.
/// </summary>
public enum AutocompleteKind
{
    /// <summary>An AI agent</summary>
    Agent,
    /// <summary>A file or content item</summary>
    File,
    /// <summary>A slash command</summary>
    Command,
    /// <summary>Other types of items</summary>
    Other
}

/// <summary>
/// Represents a single autocomplete suggestion.
/// </summary>
/// <param name="Label">Display text shown in the autocomplete dropdown.</param>
/// <param name="InsertText">Text that gets inserted when the item is selected.</param>
/// <param name="Description">Additional description shown in the dropdown.</param>
/// <param name="Category">Category for grouping (e.g., "Agents", "Files").</param>
/// <param name="Priority">Sorting priority within the category (higher = shown first).</param>
/// <param name="Kind">The kind of item (Agent, File, Command) - determines icon.</param>
public record AutocompleteItem(
    string Label,
    string InsertText,
    string? Description = null,
    string Category = "Files",
    int Priority = 0,
    AutocompleteKind Kind = AutocompleteKind.Other
);
