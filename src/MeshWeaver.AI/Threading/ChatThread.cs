namespace MeshWeaver.AI.Threading;

/// <summary>
/// Represents a chat thread with metadata.
/// </summary>
/// <param name="Id">Unique identifier for the thread</param>
/// <param name="Scope">Scope identifier (e.g., mesh node address)</param>
/// <param name="Title">Optional auto-generated or user-provided title</param>
/// <param name="CreatedAt">When the thread was created</param>
/// <param name="LastActivityAt">When the thread was last modified</param>
/// <param name="ProviderId">Which AI provider created this thread</param>
public record ChatThread(
    string Id,
    string? Scope,
    string? Title,
    DateTime CreatedAt,
    DateTime LastActivityAt,
    string? ProviderId
)
{
    /// <summary>
    /// Creates a new thread with the given ID and optional scope.
    /// </summary>
    public static ChatThread Create(string id, string? scope = null, string? providerId = null)
    {
        var now = DateTime.UtcNow;
        return new ChatThread(id, scope, null, now, now, providerId);
    }

    /// <summary>
    /// Gets a display title for the thread.
    /// Returns the title if set, otherwise a formatted date.
    /// </summary>
    public string DisplayTitle => Title ?? $"Chat from {CreatedAt:MM/dd/yyyy HH:mm}";

    /// <summary>
    /// Creates a copy with updated activity timestamp.
    /// </summary>
    public ChatThread WithActivity() => this with { LastActivityAt = DateTime.UtcNow };

    /// <summary>
    /// Creates a copy with the specified title.
    /// </summary>
    public ChatThread WithTitle(string title) => this with { Title = title, LastActivityAt = DateTime.UtcNow };
}
