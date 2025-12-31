using MeshWeaver.AI.Persistence;
using MeshWeaver.Graph.Configuration;

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
    /// Display order for sorting in model dropdown (lower = first)
    /// </summary>
    int DisplayOrder { get; }

    /// <summary>
    /// Creates a chat using the default model
    /// </summary>
    Task<IAgentChat> CreateAsync();

    /// <summary>
    /// Creates a chat using the specified model
    /// </summary>
    Task<IAgentChat> CreateAsync(string modelName);

    /// <summary>
    /// Creates a chat using the specified model and context path for hierarchical agent resolution
    /// </summary>
    Task<IAgentChat> CreateAsync(string modelName, string? contextPath);

    Task DeleteThreadAsync(string threadId);
    Task<IAgentChat> ResumeAsync(ChatConversation messages);

    /// <summary>
    /// Gets available agents for the specified context path using hierarchical resolution
    /// </summary>
    Task<IReadOnlyList<AgentConfiguration>> GetAgentsAsync(string? contextPath = null);
}
