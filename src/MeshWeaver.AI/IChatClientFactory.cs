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
    /// Returns <c>true</c> when this factory can serve <paramref name="modelName"/>.
    /// Used for factory selection per agent: the agent's <c>PreferredModel</c> drives
    /// which factory creates its chat client. The default implementation honours the
    /// legacy <see cref="Models"/> list — concrete factories should override with a
    /// shape-aware predicate (e.g. "claude-*" → AzureClaude, "*" → AzureFoundry
    /// gateway as catch-all) so routing works even when <see cref="Models"/> is empty
    /// (which is the default after the model-config-from-env-vars cleanup).
    /// </summary>
    bool Supports(string modelName) =>
        !string.IsNullOrEmpty(modelName) && Models.Any(m =>
            string.Equals(m, modelName, StringComparison.OrdinalIgnoreCase));

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
