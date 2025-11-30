namespace MeshWeaver.Blazor.Monaco;

/// <summary>
/// Represents a completion item for Monaco editor autocomplete.
/// </summary>
public class CompletionItem
{
    /// <summary>
    /// The label shown in the autocomplete dropdown.
    /// </summary>
    public required string Label { get; init; }

    /// <summary>
    /// The text to insert when the completion is selected.
    /// </summary>
    public string? InsertText { get; init; }

    /// <summary>
    /// The description shown in the autocomplete dropdown.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Additional detail text.
    /// </summary>
    public string? Detail { get; init; }

    /// <summary>
    /// The category/group for this completion item (e.g., "Agents", "Commands").
    /// </summary>
    public string? Category { get; init; }
}

/// <summary>
/// Configuration for a completion provider in Monaco editor.
/// </summary>
public class CompletionProviderConfig
{
    /// <summary>
    /// Characters that trigger the completion popup.
    /// </summary>
    public string[] TriggerCharacters { get; init; } = [];

    /// <summary>
    /// The completion items to show.
    /// </summary>
    public List<CompletionItem> Items { get; init; } = [];
}
