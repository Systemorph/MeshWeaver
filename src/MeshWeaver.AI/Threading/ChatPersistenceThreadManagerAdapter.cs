using Microsoft.Extensions.AI;
using MeshWeaver.AI.Persistence;

namespace MeshWeaver.AI.Threading;

/// <summary>
/// Adapter that bridges IChatPersistenceService to IThreadManager.
/// Maps conversations to threads for backward compatibility with existing providers.
/// </summary>
public class ChatPersistenceThreadManagerAdapter : IThreadManager
{
    private readonly IChatPersistenceService _persistenceService;

    public ChatPersistenceThreadManagerAdapter(IChatPersistenceService persistenceService)
    {
        _persistenceService = persistenceService;
    }

    public async Task<ChatThread> GetOrCreateThreadAsync(string threadId, string? scope = null, CancellationToken ct = default)
    {
        var conversation = await _persistenceService.LoadConversationAsync(threadId);

        if (conversation == null)
        {
            conversation = new ChatConversation
            {
                Id = threadId,
                Title = null,
                CreatedAt = DateTime.UtcNow,
                LastModifiedAt = DateTime.UtcNow,
                AgentContext = scope != null ? new AgentContext { Address = scope } : null
            };
            await _persistenceService.SaveConversationAsync(conversation);
        }

        return ConversationToThread(conversation, scope);
    }

    public async Task AddMessageAsync(string threadId, ChatMessage message, CancellationToken ct = default)
    {
        var conversation = await _persistenceService.LoadConversationAsync(threadId);

        if (conversation == null)
        {
            conversation = new ChatConversation { Id = threadId };
        }

        conversation = conversation.WithMessage(message);
        await _persistenceService.SaveConversationAsync(conversation);
    }

    public async Task AddMessagesAsync(string threadId, IEnumerable<ChatMessage> messages, CancellationToken ct = default)
    {
        var conversation = await _persistenceService.LoadConversationAsync(threadId);

        if (conversation == null)
        {
            conversation = new ChatConversation { Id = threadId };
        }

        conversation = conversation.WithMessages(messages);
        await _persistenceService.SaveConversationAsync(conversation);
    }

    public async Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(string threadId, CancellationToken ct = default)
    {
        var conversation = await _persistenceService.LoadConversationAsync(threadId);
        return conversation?.Messages ?? new List<ChatMessage>();
    }

    public async Task ClearThreadAsync(string threadId, CancellationToken ct = default)
    {
        var conversation = await _persistenceService.LoadConversationAsync(threadId);

        if (conversation != null)
        {
            var cleared = conversation with
            {
                Messages = new List<ChatMessage>(),
                LastModifiedAt = DateTime.UtcNow
            };
            await _persistenceService.SaveConversationAsync(cleared);
        }
    }

    public async Task<IReadOnlyList<ChatThread>> ListThreadsAsync(string? scope = null, CancellationToken ct = default)
    {
        var conversations = await _persistenceService.GetConversationsAsync();

        return conversations
            .Where(c => scope == null || c.AgentContext?.Address == scope)
            .Select(c => ConversationToThread(c, c.AgentContext?.Address))
            .OrderByDescending(t => t.LastActivityAt)
            .ToList();
    }

    public async Task DeleteThreadAsync(string threadId, CancellationToken ct = default)
    {
        await _persistenceService.DeleteConversationAsync(threadId);
    }

    public async Task<ChatThread?> GetThreadAsync(string threadId, CancellationToken ct = default)
    {
        var conversation = await _persistenceService.LoadConversationAsync(threadId);
        return conversation != null ? ConversationToThread(conversation, conversation.AgentContext?.Address) : null;
    }

    public async Task UpdateTitleAsync(string threadId, string title, CancellationToken ct = default)
    {
        await _persistenceService.UpdateConversationTitleAsync(threadId, title);
    }

    public async Task<ChatThread?> GetMostRecentThreadAsync(string? scope = null, CancellationToken ct = default)
    {
        var conversations = await _persistenceService.GetConversationsAsync();

        var mostRecent = conversations
            .Where(c => scope == null || c.AgentContext?.Address == scope)
            .OrderByDescending(c => c.LastModifiedAt)
            .FirstOrDefault();

        return mostRecent != null ? ConversationToThread(mostRecent, mostRecent.AgentContext?.Address) : null;
    }

    private static ChatThread ConversationToThread(ChatConversation conversation, string? scope)
    {
        return new ChatThread(
            Id: conversation.Id,
            Scope: scope,
            Title: conversation.Title == "New Chat" ? null : conversation.Title,
            CreatedAt: conversation.CreatedAt,
            LastActivityAt: conversation.LastModifiedAt,
            ProviderId: null
        );
    }
}
