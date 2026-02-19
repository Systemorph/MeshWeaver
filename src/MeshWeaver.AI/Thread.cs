using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.ShortGuid;

namespace MeshWeaver.AI;

/// <summary>
/// Defines the type of a thread message for rendering purposes.
/// </summary>
public enum ThreadMessageType
{
    /// <summary>
    /// User is currently editing this message (not yet submitted).
    /// Rendered with MarkdownEditorControl.
    /// </summary>
    EditingPrompt,

    /// <summary>
    /// Submitted user message.
    /// Rendered with MarkdownControl (readonly).
    /// </summary>
    ExecutedInput,

    /// <summary>
    /// Assistant/agent response message.
    /// Rendered with MarkdownControl (readonly).
    /// </summary>
    AgentResponse
}

/// <summary>
/// Content stored in Thread MeshNodes.
/// Threads are stored as MeshNodes with nodeType="Thread".
/// Title is stored in MeshNode.Name, LastModified tracks activity.
/// Messages are stored as child MeshNodes with nodeType="ThreadMessage".
/// </summary>
public record Thread : ISatelliteContent
{
    /// <summary>
    /// Serialized AgentSession state for resuming conversations.
    /// </summary>
    public JsonElement? SessionState { get; init; }

    /// <summary>
    /// Legacy: Thread messages stored inline.
    /// New threads use child MeshNodes instead.
    /// Kept for backward compatibility with existing threads.
    /// </summary>
    [Obsolete("Use child MeshNodes with nodeType=ThreadMessage instead. Kept for backward compatibility.")]
    public List<ThreadMessage>? Messages { get; init; }

    /// <summary>
    /// The path of the parent node where this thread was created.
    /// Used for navigation back to context.
    /// </summary>
    public string? ParentPath { get; init; }

    /// <summary>
    /// ISatelliteContent: permissions are checked against the parent node.
    /// </summary>
    public string? PrimaryNodePath => ParentPath;
}

/// <summary>
/// Extension methods for Thread message operations.
/// </summary>
public static class ThreadMessageExtensions
{
    /// <summary>
    /// Converts ThreadMessage collection to Microsoft.Extensions.AI.ChatMessage format.
    /// </summary>
    public static List<Microsoft.Extensions.AI.ChatMessage> ToChatMessages(this IEnumerable<ThreadMessage> messages)
    {
        return messages
            .Where(msg => msg.Type != ThreadMessageType.EditingPrompt) // Exclude editing prompts
            .Select(msg => new Microsoft.Extensions.AI.ChatMessage(
                new Microsoft.Extensions.AI.ChatRole(msg.Role),
                msg.Text)
            {
                AuthorName = msg.AuthorName
            }).ToList();
    }

    /// <summary>
    /// Creates ThreadMessage records from Microsoft.Extensions.AI.ChatMessage collection.
    /// </summary>
    public static List<ThreadMessage> FromChatMessages(
        IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
        ThreadMessageType defaultType = ThreadMessageType.ExecutedInput)
    {
        return messages.Select(msg => new ThreadMessage
        {
            Id = Guid.NewGuid().AsString(),
            Role = msg.Role.Value,
            AuthorName = msg.AuthorName,
            Text = msg.Text ?? string.Empty,
            Timestamp = DateTime.UtcNow,
            Type = msg.Role.Value.Equals("user", StringComparison.OrdinalIgnoreCase)
                ? ThreadMessageType.ExecutedInput
                : ThreadMessageType.AgentResponse
        }).ToList();
    }

    /// <summary>
    /// Converts legacy Thread with inline Messages to ChatMessages.
    /// </summary>
#pragma warning disable CS0618 // Type or member is obsolete
    public static List<Microsoft.Extensions.AI.ChatMessage> ToChatMessages(this Thread thread)
    {
        if (thread.Messages == null || thread.Messages.Count == 0)
            return [];

        return thread.Messages.ToChatMessages();
    }
#pragma warning restore CS0618

    /// <summary>
    /// Creates a legacy Thread from Microsoft.Extensions.AI.ChatMessage collection.
    /// For backward compatibility only - new code should use child MeshNodes.
    /// </summary>
    [Obsolete("Use child MeshNodes with nodeType=ThreadMessage instead.")]
    public static Thread FromChatMessagesToThread(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, string? parentPath = null)
    {
#pragma warning disable CS0618 // Type or member is obsolete
        return new Thread
        {
            Messages = FromChatMessages(messages),
            ParentPath = parentPath
        };
#pragma warning restore CS0618
    }
}

/// <summary>
/// Represents a single message in a thread conversation.
/// Stored as content of child MeshNodes under a Thread node.
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

    /// <summary>
    /// The type of this message for rendering purposes.
    /// Defaults to ExecutedInput for backward compatibility.
    /// </summary>
    public ThreadMessageType Type { get; init; } = ThreadMessageType.ExecutedInput;
}
