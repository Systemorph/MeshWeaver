namespace MeshWeaver.AI;

/// <summary>
/// View model for thread data binding in the ThreadChatControl.
/// Wraps all thread state needed by the Blazor view:
/// - Messages: ordered list of child message node IDs
/// - ThreadPath: the thread node's full path
/// - InitialContext: the context path for agent initialization
/// - InitialContextDisplayName: display name for the context chip
///
/// Serialized with $type via ObjectPolymorphicConverter, so GetStream&lt;object&gt;
/// can deserialize it (raw arrays can't be deserialized as object).
/// </summary>
public record ThreadViewModel
{
    public IReadOnlyList<string> Messages { get; init; } = [];
    public string? ThreadPath { get; init; }
    public string? InitialContext { get; init; }
    public string? InitialContextDisplayName { get; init; }
}
