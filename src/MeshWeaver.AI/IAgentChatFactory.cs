using MeshWeaver.AI.Persistence;

namespace MeshWeaver.AI;

public interface IAgentChatFactory
{
    Task<IAgentChat> CreateAsync();
    Task DeleteThreadAsync(string threadId);
    Task<IAgentChat> ResumeAsync(ChatConversation messages);
    Task<IReadOnlyDictionary<string, IAgentDefinition>> GetAgentsAsync();
}
