using System.Collections.Concurrent;
using MeshWeaver.Messaging;

namespace MeshWeaver.AI.Persistence;

/// <summary>
/// In-memory implementation of chat persistence service
/// Stores conversations and agent chat states in memory for the duration of the application
/// Supports per-user conversation isolation
/// </summary>
public class InMemoryChatPersistenceService(IAgentChatFactory agentChatFactory, AccessService accessService) : IChatPersistenceService
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ChatConversation>> userConversations = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, IAgentChat>> userAgentChatStates = new();

    private string GetCurrentUserId()
    {
        var context = accessService.Context;
        return context?.ObjectId ?? "anonymous";
    }

    private ConcurrentDictionary<string, ChatConversation> GetUserConversations()
    {
        var userId = GetCurrentUserId();
        return userConversations.GetOrAdd(userId, _ => new ConcurrentDictionary<string, ChatConversation>());
    }

    private ConcurrentDictionary<string, IAgentChat> GetUserAgentChatStates()
    {
        var userId = GetCurrentUserId();
        return userAgentChatStates.GetOrAdd(userId, _ => new ConcurrentDictionary<string, IAgentChat>());
    }
    public Task<ChatConversation> SaveConversationAsync(ChatConversation conversation, IAgentChat? agentChat = null)
    {
        if (conversation == null)
            throw new ArgumentNullException(nameof(conversation));

        // Update the last modified time
        var updatedConversation = conversation with { LastModifiedAt = DateTime.UtcNow };

        // Store the conversation for the current user
        var conversations = GetUserConversations();
        conversations.AddOrUpdate(updatedConversation.Id, updatedConversation, (_, _) => updatedConversation);

        // Store agent chat state if provided
        if (agentChat != null)
        {
            SaveAgentChatState(updatedConversation.Id, agentChat);
        }

        return Task.FromResult(updatedConversation);
    }

    public Task<ChatConversation?> LoadConversationAsync(string conversationId)
    {
        if (string.IsNullOrEmpty(conversationId))
            throw new ArgumentException("Conversation ID cannot be null or empty", nameof(conversationId));

        var conversations = GetUserConversations();
        conversations.TryGetValue(conversationId, out var conversation);
        return Task.FromResult(conversation);
    }

    public async Task<IAgentChat> RestoreAgentChatAsync(string conversationId)
    {
        var agentChatStates = GetUserAgentChatStates();
        if (agentChatStates.TryGetValue(conversationId, out var ret))
            return ret;

        var conversations = GetUserConversations();
        return conversations.TryGetValue(conversationId, out var conv)
            ? await agentChatFactory.ResumeAsync(conv!)
            : await agentChatFactory.CreateAsync();
    }
    public Task<List<ChatConversation>> GetConversationsAsync()
    {
        var conversations = GetUserConversations();
        var ret = conversations.Values
            .OrderByDescending(c => c?.LastModifiedAt)
            .ToList();

        return Task.FromResult(ret);
    }

    public Task<bool> DeleteConversationAsync(string conversationId)
    {
        if (string.IsNullOrEmpty(conversationId))
            return Task.FromResult(false);

        var conversations = GetUserConversations();
        var agentChatStates = GetUserAgentChatStates();

        var conversationRemoved = conversations.TryRemove(conversationId, out _);
        agentChatStates.TryRemove(conversationId, out _);

        return Task.FromResult(conversationRemoved);
    }

    public async Task<ChatConversation?> GetMostRecentConversationAsync()
    {
        var ret = await GetConversationsAsync();
        return ret.FirstOrDefault();
    }

    public async Task<ChatConversation?> UpdateConversationTitleAsync(string conversationId, string? newTitle)
    {
        if (string.IsNullOrEmpty(conversationId))
            throw new ArgumentException("Conversation ID cannot be null or empty", nameof(conversationId));

        var conversation = await LoadConversationAsync(conversationId);
        if (conversation == null)
            return null;

        var updatedConversation = conversation with
        {
            Title = newTitle ?? "Untitled Chat",
            LastModifiedAt = DateTime.UtcNow
        };

        return await SaveConversationAsync(updatedConversation);
    }    /// <summary>
         /// Stores the current state of an AgentChat for later restoration
         /// </summary>
    private void SaveAgentChatState(string conversationId, IAgentChat? agentChat)
    {
        try
        {
            if (agentChat is not null)
            {
                var agentChatStates = GetUserAgentChatStates();
                agentChatStates[conversationId] = agentChat;
            }
        }
        catch (Exception)
        {
            // Log error in real implementation
            // For now, silently continue as this is not critical for basic functionality
        }
    }

    /// <summary>
    /// Clears all stored conversations and agent states for the current user
    /// Useful for testing or when starting fresh
    /// </summary>
    public void ClearAll()
    {
        var conversations = GetUserConversations();
        var agentChatStates = GetUserAgentChatStates();
        conversations.Clear();
        agentChatStates.Clear();
    }

    /// <summary>
    /// Gets the current number of stored conversations for the current user
    /// </summary>
    public int ConversationCount
    {
        get
        {
            var conversations = GetUserConversations();
            return conversations.Count;
        }
    }

    private readonly ConcurrentDictionary<string, System.Text.Json.JsonElement> threadStorage = new();

    public Task SaveThreadAsync(string threadId, string agentName, System.Text.Json.JsonElement serializedThread)
    {
        var key = $"{GetCurrentUserId()}:{threadId}:{agentName}";
        threadStorage[key] = serializedThread;
        return Task.CompletedTask;
    }

    public Task<System.Text.Json.JsonElement?> LoadThreadAsync(string threadId, string agentName)
    {
        var key = $"{GetCurrentUserId()}:{threadId}:{agentName}";
        if (threadStorage.TryGetValue(key, out var thread))
        {
            return Task.FromResult<System.Text.Json.JsonElement?>(thread);
        }
        return Task.FromResult<System.Text.Json.JsonElement?>(null);
    }
}
