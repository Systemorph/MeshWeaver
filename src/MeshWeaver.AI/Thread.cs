using System.Text.Json;

namespace MeshWeaver.AI;

/// <summary>
/// Content stored in Thread MeshNodes.
/// Threads are stored as MeshNodes with nodeType="Thread" in User/{userId}/Threads/.
/// Title is stored in MeshNode.Name, LastModified tracks activity.
/// </summary>
public record Thread
{
    /// <summary>
    /// Serialized AgentSession state for resuming conversations.
    /// </summary>
    public JsonElement? SessionState { get; init; }

    /// <summary>
    /// Thread messages stored inline.
    /// </summary>
    public List<ThreadMessage>? Messages { get; init; }
}

/// <summary>
/// Represents a single message in a thread conversation.
/// </summary>
public record ThreadMessage
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
