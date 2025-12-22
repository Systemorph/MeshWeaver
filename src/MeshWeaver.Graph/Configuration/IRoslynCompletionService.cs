namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides Roslyn-based code completion for C# source code.
/// Used by Monaco editor for IntelliSense support.
/// </summary>
public interface IRoslynCompletionService
{
    /// <summary>
    /// Gets completion items at the specified position in the source code.
    /// </summary>
    /// <param name="sourceCode">The current source code</param>
    /// <param name="position">The cursor position (character offset)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of completion items</returns>
    Task<IReadOnlyList<CompletionItem>> GetCompletionsAsync(
        string sourceCode,
        int position,
        CancellationToken ct = default);
}

/// <summary>
/// Represents a code completion item.
/// </summary>
public record CompletionItem
{
    /// <summary>
    /// The text to display in the completion list.
    /// </summary>
    public required string Label { get; init; }

    /// <summary>
    /// The text to insert when selected.
    /// </summary>
    public required string InsertText { get; init; }

    /// <summary>
    /// The kind of completion (Method, Property, Class, etc.).
    /// Maps to Monaco's CompletionItemKind.
    /// </summary>
    public CompletionItemKind Kind { get; init; }

    /// <summary>
    /// Additional details/documentation about the item.
    /// </summary>
    public string? Detail { get; init; }

    /// <summary>
    /// Sort order text for prioritizing completion items.
    /// </summary>
    public string? SortText { get; init; }

    /// <summary>
    /// Filter text used for matching.
    /// </summary>
    public string? FilterText { get; init; }
}

/// <summary>
/// Completion item kinds that map to Monaco editor's CompletionItemKind.
/// </summary>
public enum CompletionItemKind
{
    Method = 0,
    Function = 1,
    Constructor = 2,
    Field = 3,
    Variable = 4,
    Class = 5,
    Struct = 6,
    Interface = 7,
    Module = 8,
    Property = 9,
    Event = 10,
    Operator = 11,
    Unit = 12,
    Value = 13,
    Constant = 14,
    Enum = 15,
    EnumMember = 16,
    Keyword = 17,
    Text = 18,
    Color = 19,
    File = 20,
    Reference = 21,
    Customcolor = 22,
    Folder = 23,
    TypeParameter = 24,
    User = 25,
    Issue = 26,
    Snippet = 27
}
