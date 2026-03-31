using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using MeshWeaver.Messaging;

namespace MeshWeaver.AI.Threading;

/// <summary>
/// In-memory implementation of IThreadManager.
/// Stores threads and messages in memory with per-user isolation.
/// </summary>
public class InMemoryThreadManager : IThreadManager
{
    private readonly AccessService _accessService;

    // Per-user storage: userId -> (threadId -> thread)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ChatThread>> _userThreads = new();
    // Per-user message storage: userId -> (threadId -> messages)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, List<ChatMessage>>> _userMessages = new();

    public InMemoryThreadManager(AccessService accessService)
    {
        _accessService = accessService;
    }

    private string GetCurrentUserId()
    {
        var context = _accessService.Context ?? _accessService.CircuitContext;
        return context?.ObjectId ?? "anonymous";
    }

    private ConcurrentDictionary<string, ChatThread> GetUserThreads()
    {
        var userId = GetCurrentUserId();
        return _userThreads.GetOrAdd(userId, _ => new ConcurrentDictionary<string, ChatThread>());
    }

    private ConcurrentDictionary<string, List<ChatMessage>> GetUserMessages()
    {
        var userId = GetCurrentUserId();
        return _userMessages.GetOrAdd(userId, _ => new ConcurrentDictionary<string, List<ChatMessage>>());
    }

    public Task<ChatThread> GetOrCreateThreadAsync(string threadId, string? scope = null, CancellationToken ct = default)
    {
        var threads = GetUserThreads();
        var messages = GetUserMessages();

        var thread = threads.GetOrAdd(threadId, _ =>
        {
            messages.TryAdd(threadId, new List<ChatMessage>());
            return ChatThread.Create(threadId, scope);
        });

        return Task.FromResult(thread);
    }

    public Task AddMessageAsync(string threadId, ChatMessage message, CancellationToken ct = default)
    {
        var threads = GetUserThreads();
        var messages = GetUserMessages();

        // Ensure thread exists
        threads.AddOrUpdate(threadId,
            _ =>
            {
                messages.TryAdd(threadId, new List<ChatMessage>());
                return ChatThread.Create(threadId);
            },
            (_, existing) => existing.WithActivity());

        // Add message
        if (messages.TryGetValue(threadId, out var messageList))
        {
            lock (messageList)
            {
                messageList.Add(message);
            }
        }

        // Auto-title from first user message
        if (threads.TryGetValue(threadId, out var thread) && thread.Title == null)
        {
            if (message.Role == ChatRole.User && !string.IsNullOrWhiteSpace(message.Text))
            {
                var title = message.Text.Length > 50 ? message.Text[..50] + "..." : message.Text;
                threads[threadId] = thread.WithTitle(title);
            }
        }

        return Task.CompletedTask;
    }

    public Task AddMessagesAsync(string threadId, IEnumerable<ChatMessage> messagesToAdd, CancellationToken ct = default)
    {
        var threads = GetUserThreads();
        var messages = GetUserMessages();

        // Ensure thread exists
        threads.AddOrUpdate(threadId,
            _ =>
            {
                messages.TryAdd(threadId, new List<ChatMessage>());
                return ChatThread.Create(threadId);
            },
            (_, existing) => existing.WithActivity());

        // Add messages
        if (messages.TryGetValue(threadId, out var messageList))
        {
            lock (messageList)
            {
                messageList.AddRange(messagesToAdd);
            }
        }

        // Auto-title from first user message
        if (threads.TryGetValue(threadId, out var thread) && thread.Title == null)
        {
            var firstUserMessage = messagesToAdd.FirstOrDefault(m =>
                m.Role == ChatRole.User && !string.IsNullOrWhiteSpace(m.Text));
            if (firstUserMessage != null)
            {
                var title = firstUserMessage.Text.Length > 50
                    ? firstUserMessage.Text[..50] + "..."
                    : firstUserMessage.Text;
                threads[threadId] = thread.WithTitle(title);
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(string threadId, CancellationToken ct = default)
    {
        var messages = GetUserMessages();

        if (messages.TryGetValue(threadId, out var messageList))
        {
            lock (messageList)
            {
                return Task.FromResult<IReadOnlyList<ChatMessage>>(messageList.ToList());
            }
        }

        return Task.FromResult<IReadOnlyList<ChatMessage>>(Array.Empty<ChatMessage>());
    }

    public Task ClearThreadAsync(string threadId, CancellationToken ct = default)
    {
        var threads = GetUserThreads();
        var messages = GetUserMessages();

        if (messages.TryGetValue(threadId, out var messageList))
        {
            lock (messageList)
            {
                messageList.Clear();
            }
        }

        // Update thread activity
        if (threads.TryGetValue(threadId, out var thread))
        {
            threads[threadId] = thread.WithActivity();
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ChatThread>> ListThreadsAsync(string? scope = null, CancellationToken ct = default)
    {
        var threads = GetUserThreads();

        var result = threads.Values
            .Where(t => scope == null || t.Scope == scope)
            .OrderByDescending(t => t.LastActivityAt)
            .ToList();

        return Task.FromResult<IReadOnlyList<ChatThread>>(result);
    }

    public Task DeleteThreadAsync(string threadId, CancellationToken ct = default)
    {
        var threads = GetUserThreads();
        var messages = GetUserMessages();

        threads.TryRemove(threadId, out _);
        messages.TryRemove(threadId, out _);

        return Task.CompletedTask;
    }

    public Task<ChatThread?> GetThreadAsync(string threadId, CancellationToken ct = default)
    {
        var threads = GetUserThreads();

        threads.TryGetValue(threadId, out var thread);
        return Task.FromResult(thread);
    }

    public Task UpdateTitleAsync(string threadId, string title, CancellationToken ct = default)
    {
        var threads = GetUserThreads();

        if (threads.TryGetValue(threadId, out var thread))
        {
            threads[threadId] = thread.WithTitle(title);
        }

        return Task.CompletedTask;
    }

    public Task<ChatThread?> GetMostRecentThreadAsync(string? scope = null, CancellationToken ct = default)
    {
        var threads = GetUserThreads();

        var mostRecent = threads.Values
            .Where(t => scope == null || t.Scope == scope)
            .OrderByDescending(t => t.LastActivityAt)
            .FirstOrDefault();

        return Task.FromResult(mostRecent);
    }

    /// <summary>
    /// Clears all threads and messages for the current user.
    /// </summary>
    public void ClearAll()
    {
        var threads = GetUserThreads();
        var messages = GetUserMessages();
        threads.Clear();
        messages.Clear();
    }
}
