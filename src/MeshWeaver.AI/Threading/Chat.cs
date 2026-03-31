using System.Text.Json;

namespace MeshWeaver.AI.Threading;

/// <summary>
/// Represents a persisted chat record stored in the user's chat partition.
/// Contains the thread metadata and serialized messages.
/// </summary>
public record Chat
{
    /// <summary>
    /// Unique identifier for the chat (thread ID).
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Optional scope identifier (e.g., mesh node address).
    /// </summary>
    public string? Scope { get; init; }

    /// <summary>
    /// Optional auto-generated or user-provided title.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// When the chat was created.
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// When the chat was last modified.
    /// </summary>
    public DateTime LastActivityAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Which AI provider created this chat.
    /// </summary>
    public string? ProviderId { get; init; }

    /// <summary>
    /// Serialized messages as JSON array.
    /// </summary>
    public JsonElement? Messages { get; init; }

    /// <summary>
    /// Gets a display title for the chat.
    /// </summary>
    public string DisplayTitle => Title ?? $"Chat from {CreatedAt:MM/dd/yyyy HH:mm}";

    /// <summary>
    /// Converts this Chat to a ChatThread (without messages).
    /// </summary>
    public ChatThread ToThread() => new(
        Id: Id,
        Scope: Scope,
        Title: Title,
        CreatedAt: CreatedAt,
        LastActivityAt: LastActivityAt,
        ProviderId: ProviderId
    );

    /// <summary>
    /// Creates a Chat from a ChatThread.
    /// </summary>
    public static Chat FromThread(ChatThread thread, JsonElement? messages = null) => new()
    {
        Id = thread.Id,
        Scope = thread.Scope,
        Title = thread.Title,
        CreatedAt = thread.CreatedAt,
        LastActivityAt = thread.LastActivityAt,
        ProviderId = thread.ProviderId,
        Messages = messages
    };
}
