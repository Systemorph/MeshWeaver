using Microsoft.Extensions.AI;

namespace MeshWeaver.AI.Persistence;

/// <summary>
/// Represents a complete chat conversation with metadata
/// </summary>
public record ChatConversation
{
    /// <summary>Unique conversation identifier; defaults to a new GUID.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString();
    /// <summary>User-facing conversation title; defaults to "New Chat".</summary>
    public string Title { get; init; } = "New Chat";
    /// <summary>UTC timestamp when the conversation was created.</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    /// <summary>UTC timestamp of the most recent message addition.</summary>
    public DateTime LastModifiedAt { get; init; } = DateTime.UtcNow;
    /// <summary>The ordered chat messages making up the conversation.</summary>
    public List<ChatMessage> Messages { get; init; } = new();
    /// <summary>The agent context this conversation runs under; null when not bound to an agent.</summary>
    public AgentContext? AgentContext { get; init; }

    /// <summary>
    /// Gets a display title for the conversation based on the first user message or timestamp
    /// </summary>
    public string DisplayTitle
    {
        get
        {
            var firstUserMessage = Messages.FirstOrDefault(m =>
                m.Role.Value.Equals("User", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(m.Text));

            if (firstUserMessage != null)
            {
                // Take first 50 characters of the first user message
                var preview = firstUserMessage.Text.Length > 50
                    ? firstUserMessage.Text[..50] + "..."
                    : firstUserMessage.Text;
                return preview;
            }

            return $"Chat from {CreatedAt:MM/dd/yyyy HH:mm}";
        }
    }

    /// <summary>
    /// Creates a new conversation with the given context
    /// </summary>
    public static ChatConversation CreateNew(AgentContext? context = null)
    {
        return new ChatConversation
        {
            AgentContext = context,
            CreatedAt = DateTime.UtcNow,
            LastModifiedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Updates the conversation with a new message
    /// </summary>
    public ChatConversation WithMessage(ChatMessage message)
    {
        var updatedMessages = Messages.ToList();
        updatedMessages.Add(message);

        return this with
        {
            Messages = updatedMessages,
            LastModifiedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Updates the conversation with multiple new messages
    /// </summary>
    public ChatConversation WithMessages(IEnumerable<ChatMessage> newMessages)
    {
        var updatedMessages = Messages.ToList();
        updatedMessages.AddRange(newMessages);

        return this with
        {
            Messages = updatedMessages,
            LastModifiedAt = DateTime.UtcNow
        };
    }
}
