using System.Text.Json;

namespace MeshWeaver.Mesh;

/// <summary>
/// Content stored in Thread MeshNodes.
/// Threads are stored as MeshNodes with nodeType="Thread" in User/{userId}/Threads/.
/// </summary>
public record ThreadNodeContent
{
    /// <summary>
    /// Optional title (auto-generated from first message if not set).
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// When the thread was created.
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// When the thread was last active.
    /// </summary>
    public DateTime LastActivityAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// The AI provider/model used for this thread.
    /// </summary>
    public string? ProviderId { get; init; }

    /// <summary>
    /// Serialized AgentSession state for resuming conversations.
    /// </summary>
    public JsonElement? SessionState { get; init; }

    /// <summary>
    /// Thread messages stored inline.
    /// </summary>
    public List<ThreadMessageContent>? Messages { get; init; }

    /// <summary>
    /// Gets a display title for the thread.
    /// </summary>
    public string DisplayTitle => Title ?? $"Thread from {CreatedAt:MM/dd/yyyy HH:mm}";
}

/// <summary>
/// Represents a single message in a thread conversation.
/// </summary>
public record ThreadMessageContent
{
    /// <summary>
    /// Unique identifier for this message.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// The role of the message sender: "user", "assistant", or "system".
    /// </summary>
    public required string Role { get; init; }

    /// <summary>
    /// Optional author name for multi-agent conversations.
    /// </summary>
    public string? AuthorName { get; init; }

    /// <summary>
    /// The text content of the message.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// When the message was created.
    /// </summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// If this message triggered a delegation, path to the sub-thread node.
    /// </summary>
    public string? DelegationPath { get; init; }
}
