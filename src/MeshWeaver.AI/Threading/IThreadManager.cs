using Microsoft.Extensions.AI;

namespace MeshWeaver.AI.Threading;

/// <summary>
/// Interface for managing chat threads across all AI providers.
/// Provides a unified abstraction for thread lifecycle management.
/// </summary>
public interface IThreadManager
{
    /// <summary>
    /// Gets an existing thread or creates a new one if it doesn't exist.
    /// </summary>
    /// <param name="threadId">The unique identifier for the thread</param>
    /// <param name="scope">Optional scope identifier (e.g., mesh node address)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The thread record</returns>
    Task<ChatThread> GetOrCreateThreadAsync(string threadId, string? scope = null, CancellationToken ct = default);

    /// <summary>
    /// Adds a message to a thread.
    /// </summary>
    /// <param name="threadId">The thread to add the message to</param>
    /// <param name="message">The message to add</param>
    /// <param name="ct">Cancellation token</param>
    Task AddMessageAsync(string threadId, ChatMessage message, CancellationToken ct = default);

    /// <summary>
    /// Adds multiple messages to a thread.
    /// </summary>
    /// <param name="threadId">The thread to add messages to</param>
    /// <param name="messages">The messages to add</param>
    /// <param name="ct">Cancellation token</param>
    Task AddMessagesAsync(string threadId, IEnumerable<ChatMessage> messages, CancellationToken ct = default);

    /// <summary>
    /// Gets all messages in a thread.
    /// </summary>
    /// <param name="threadId">The thread to get messages from</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of messages in the thread</returns>
    Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(string threadId, CancellationToken ct = default);

    /// <summary>
    /// Clears all messages from a thread while keeping the thread itself.
    /// </summary>
    /// <param name="threadId">The thread to clear</param>
    /// <param name="ct">Cancellation token</param>
    Task ClearThreadAsync(string threadId, CancellationToken ct = default);

    /// <summary>
    /// Lists all threads in a given scope.
    /// </summary>
    /// <param name="scope">The scope to list threads for (null for all threads)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of thread IDs</returns>
    Task<IReadOnlyList<ChatThread>> ListThreadsAsync(string? scope = null, CancellationToken ct = default);

    /// <summary>
    /// Deletes a thread and all its messages.
    /// </summary>
    /// <param name="threadId">The thread to delete</param>
    /// <param name="ct">Cancellation token</param>
    Task DeleteThreadAsync(string threadId, CancellationToken ct = default);

    /// <summary>
    /// Gets a thread by ID, or null if it doesn't exist.
    /// </summary>
    /// <param name="threadId">The thread ID to look up</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The thread if found, null otherwise</returns>
    Task<ChatThread?> GetThreadAsync(string threadId, CancellationToken ct = default);

    /// <summary>
    /// Updates the title of a thread.
    /// </summary>
    /// <param name="threadId">The thread to update</param>
    /// <param name="title">The new title</param>
    /// <param name="ct">Cancellation token</param>
    Task UpdateTitleAsync(string threadId, string title, CancellationToken ct = default);

    /// <summary>
    /// Gets the most recent thread in a scope.
    /// </summary>
    /// <param name="scope">The scope to search in (null for all threads)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The most recent thread, or null if none exist</returns>
    Task<ChatThread?> GetMostRecentThreadAsync(string? scope = null, CancellationToken ct = default);
}
