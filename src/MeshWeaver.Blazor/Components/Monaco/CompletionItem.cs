namespace MeshWeaver.Blazor.Components.Monaco;

/// <summary>
/// Kind of completion item - maps to Monaco's CompletionItemKind.
/// </summary>
public enum CompletionItemKind
{
    /// <summary>Default/unknown kind</summary>
    Text = 0,
    /// <summary>AI Agent or module</summary>
    Module = 8,
    /// <summary>File reference</summary>
    File = 16,
    /// <summary>Command or function</summary>
    Function = 2,
    /// <summary>Variable or reference</summary>
    Variable = 5,
    /// <summary>Folder or directory</summary>
    Folder = 23
}

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

    /// <summary>
    /// The kind of completion item (determines icon in Monaco).
    /// </summary>
    public CompletionItemKind Kind { get; init; } = CompletionItemKind.Text;

    /// <summary>
    /// The full path shown on the second line in two-line display mode.
    /// </summary>
    public string? Path { get; init; }

    /// <summary>
    /// Optional icon URL for custom icons.
    /// </summary>
    public string? IconUrl { get; init; }

    /// <summary>
    /// Sort key for controlling order in the Monaco widget.
    /// Lower values sort first. Format: "0001_label" for high priority, "9999_label" for low.
    /// When null, Monaco uses the label for sorting.
    /// </summary>
    public string? SortKey { get; init; }
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
