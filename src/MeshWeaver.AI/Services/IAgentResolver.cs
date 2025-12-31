using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;

namespace MeshWeaver.AI.Services;

/// <summary>
/// Resolves agent configurations from the graph with hierarchical lookup.
/// Agents are stored as MeshNodes with nodeType="Agent" and Content=AgentConfiguration.
/// Resolution searches upward through namespaces - most specific namespace wins.
/// </summary>
public interface IAgentResolver
{
    /// <summary>
    /// Gets all agent configurations visible from the given context path.
    /// Searches upward through namespaces: /a/b/c → /a/b → /a → / (root)
    /// Agents at more specific namespaces override parent namespace agents with same Id.
    /// </summary>
    /// <param name="contextPath">The current context path (e.g., "pricing/MS-2024")</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>All visible agents, ordered by DisplayOrder then Id</returns>
    Task<IReadOnlyList<AgentConfiguration>> GetAgentsForContextAsync(
        string? contextPath,
        CancellationToken ct = default);

    /// <summary>
    /// Gets a specific agent by its path.
    /// Path can be absolute ("/MeshNavigator") or relative from context ("RiskImportAgent").
    /// </summary>
    /// <param name="agentPath">Path to the agent</param>
    /// <param name="contextPath">Current context for relative path resolution</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The agent configuration or null if not found</returns>
    Task<AgentConfiguration?> GetAgentAsync(
        string agentPath,
        string? contextPath = null,
        CancellationToken ct = default);

    /// <summary>
    /// Gets the default agent visible from the given context.
    /// Searches upward for an agent with IsDefault=true.
    /// </summary>
    /// <param name="contextPath">The current context path</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The default agent or null if none configured</returns>
    Task<AgentConfiguration?> GetDefaultAgentAsync(
        string? contextPath = null,
        CancellationToken ct = default);

    /// <summary>
    /// Gets all agents exposed for delegation from the default agent.
    /// Returns agents with ExposedInNavigator=true.
    /// </summary>
    /// <param name="contextPath">The current context path</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Agents available for delegation</returns>
    Task<IReadOnlyList<AgentConfiguration>> GetExposedAgentsAsync(
        string? contextPath = null,
        CancellationToken ct = default);

    /// <summary>
    /// Finds agents matching the given context using ContextMatchPattern.
    /// </summary>
    /// <param name="context">The current agent context (address, layout area)</param>
    /// <param name="contextPath">The current context path</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Agents whose ContextMatchPattern matches the context</returns>
    Task<IReadOnlyList<AgentConfiguration>> FindMatchingAgentsAsync(
        AgentContext context,
        string? contextPath = null,
        CancellationToken ct = default);
}
