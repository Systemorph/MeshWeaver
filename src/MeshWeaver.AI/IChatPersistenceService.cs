namespace MeshWeaver.AI;

/// <summary>
/// Service interface for managing chat conversation persistence
/// </summary>
public interface IChatPersistenceService
{
    /// <summary>
    /// Saves a chat conversation with its AgentChat state
    /// </summary>
    Task<ChatConversation> SaveConversationAsync(ChatConversation conversation, IAgentChat? agentChat = null);

    /// <summary>
    /// Loads a specific chat conversation by ID
    /// </summary>
    Task<ChatConversation?> LoadConversationAsync(string conversationId);

    /// <summary>
    /// Restores an AgentChat from saved state
    /// </summary>
    Task<IAgentChat> RestoreAgentChatAsync(string conversationId);

    /// <summary>
    /// Gets all saved chat conversations, ordered by last modified date
    /// </summary>
    Task<List<ChatConversation>> GetConversationsAsync();

    /// <summary>
    /// Deletes a chat conversation
    /// </summary>
    Task<bool> DeleteConversationAsync(string conversationId);

    /// <summary>
    /// Gets the most recent conversation, if any
    /// </summary>
    Task<ChatConversation?> GetMostRecentConversationAsync();

    /// <summary>
    /// Updates the title of a conversation
    /// </summary>
    Task<ChatConversation?> UpdateConversationTitleAsync(string conversationId, string newTitle);
}
