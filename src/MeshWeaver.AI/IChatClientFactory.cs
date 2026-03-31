using Microsoft.Agents.AI;

namespace MeshWeaver.AI;

/// <summary>
/// Factory for creating individual ChatClientAgent instances.
/// This is the single point for creating AI agents from configurations.
/// </summary>
public interface IChatClientFactory
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
    int Order { get; }

    /// <summary>
    /// Whether this factory creates persistent agents with server-side thread management.
    /// When true, conversation history is maintained server-side and only new messages need to be sent.
    /// </summary>
    bool IsPersistent => false;

    /// <summary>
    /// Creates a ChatClientAgent for the given configuration.
    /// </summary>
    /// <param name="config">The agent configuration</param>
    /// <param name="chat">The agent chat context for tool access</param>
    /// <param name="existingAgents">Already created agents for delegation support</param>
    /// <param name="hierarchyAgents">All agent configurations in the hierarchy</param>
    /// <param name="modelName">Optional model name override</param>
    /// <returns>A configured ChatClientAgent instance</returns>
    Task<ChatClientAgent> CreateAgentAsync(
        AgentConfiguration config,
        IAgentChat chat,
        IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
        IReadOnlyList<AgentConfiguration> hierarchyAgents,
        string? modelName = null);

    /// <summary>
    /// Creates a ChatClientAgent synchronously — no await, no deadlock.
    /// Default implementation delegates to CreateAgentAsync (for backward compatibility).
    /// </summary>
    ChatClientAgent CreateAgent(
        AgentConfiguration config,
        IAgentChat chat,
        IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
        IReadOnlyList<AgentConfiguration> hierarchyAgents,
        string? modelName = null);
}
