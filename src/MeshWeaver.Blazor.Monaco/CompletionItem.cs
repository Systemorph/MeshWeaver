namespace MeshWeaver.Blazor.Monaco;

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
    Variable = 5
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
