using System.Collections.Concurrent;

namespace MeshWeaver.AI;

/// <summary>
/// In-memory implementation of chat persistence service
/// Stores conversations and agent chat states in memory for the duration of the application
/// </summary>
public class InMemoryChatPersistenceService(IAgentChatFactory agentChatFactory) : IChatPersistenceService
{
    private readonly ConcurrentDictionary<string, ChatConversation> conversations = new();
    private readonly ConcurrentDictionary<string, IAgentChat> agentChatStates = new();
    public Task<ChatConversation> SaveConversationAsync(ChatConversation conversation, IAgentChat? agentChat = null)
    {
        if (conversation == null)
            throw new ArgumentNullException(nameof(conversation));

        // Update the last modified time
        var updatedConversation = conversation with { LastModifiedAt = DateTime.UtcNow };

        // Store the conversation
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

        conversations.TryGetValue(conversationId, out var conversation);
        return Task.FromResult(conversation);
    }

    public async Task<IAgentChat> RestoreAgentChatAsync(string conversationId)
    {
        if (agentChatStates.TryGetValue(conversationId, out var ret))
            return ret;

        return conversations.TryGetValue(conversationId, out var conv)
            ? await agentChatFactory.ResumeAsync(conv!)
            : await agentChatFactory.CreateAsync();
    }

    public Task<List<ChatConversation>> GetConversationsAsync()
    {
        var ret = this.conversations.Values
            .OrderByDescending(c => c?.LastModifiedAt)
            .ToList();

        return Task.FromResult(ret);
    }

    public Task<bool> DeleteConversationAsync(string conversationId)
    {
        if (string.IsNullOrEmpty(conversationId))
            return Task.FromResult(false);

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
            if(agentChat is not null)
                agentChatStates[conversationId] = agentChat;
        }
        catch (Exception)
        {
            // Log error in real implementation
            // For now, silently continue as this is not critical for basic functionality
        }
    }

    /// <summary>
    /// Clears all stored conversations and agent states
    /// Useful for testing or when starting fresh
    /// </summary>
    public void ClearAll()
    {
        conversations.Clear();
        agentChatStates.Clear();
    }

    /// <summary>
    /// Gets the current number of stored conversations
    /// </summary>
    public int ConversationCount => conversations.Count;
}
