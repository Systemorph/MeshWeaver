using MeshWeaver.AI.Persistence;

namespace MeshWeaver.AI;

public interface IAgentChatFactory
{
    /// <summary>
    /// Factory identifier (e.g., "Azure OpenAI", "Azure Claude")
    /// </summary>
    string Name { get; }

    /// <summary>
    /// List of models this factory can create
    /// </summary>
    IReadOnlyList<string> Models { get; }

    /// <summary>
    /// Creates a chat using the default model
    /// </summary>
    Task<IAgentChat> CreateAsync();

    /// <summary>
    /// Creates a chat using the specified model
    /// </summary>
    Task<IAgentChat> CreateAsync(string modelName);

    Task DeleteThreadAsync(string threadId);
    Task<IAgentChat> ResumeAsync(ChatConversation messages);
    Task<IReadOnlyDictionary<string, IAgentDefinition>> GetAgentsAsync();
}
