using System.Text.Json;
using MeshWeaver.ShortGuid;

namespace MeshWeaver.AI;

/// <summary>
/// Content stored in Thread MeshNodes.
/// Threads are stored as MeshNodes with nodeType="Thread".
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

    /// <summary>
    /// The path of the parent node where this thread was created.
    /// Used for navigation back to context.
    /// </summary>
    public string? ParentPath { get; init; }

    /// <summary>
    /// Converts Thread messages to Microsoft.Extensions.AI.ChatMessage format.
    /// </summary>
    public List<Microsoft.Extensions.AI.ChatMessage> ToChatMessages()
    {
        if (Messages == null || Messages.Count == 0)
            return [];

        return Messages.Select(msg => new Microsoft.Extensions.AI.ChatMessage(
            new Microsoft.Extensions.AI.ChatRole(msg.Role),
            msg.Text)
        {
            AuthorName = msg.AuthorName
        }).ToList();
    }

    /// <summary>
    /// Creates a Thread from Microsoft.Extensions.AI.ChatMessage collection.
    /// </summary>
    public static Thread FromChatMessages(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, string? parentPath = null)
    {
        return new Thread
        {
            Messages = messages.Select(msg => new ThreadMessage
            {
                Id = Guid.NewGuid().AsString(),
                Role = msg.Role.Value,
                AuthorName = msg.AuthorName,
                Text = msg.Text ?? string.Empty,
                Timestamp = DateTime.UtcNow
            }).ToList(),
            ParentPath = parentPath
        };
    }

    /// <summary>
    /// Adds messages from ChatMessage collection to this thread.
    /// </summary>
    public Thread WithMessages(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages)
    {
        var newMessages = messages.Select(msg => new ThreadMessage
        {
            Id = Guid.NewGuid().AsString(),
            Role = msg.Role.Value,
            AuthorName = msg.AuthorName,
            Text = msg.Text ?? string.Empty,
            Timestamp = DateTime.UtcNow
        }).ToList();

        return this with { Messages = newMessages };
    }
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
