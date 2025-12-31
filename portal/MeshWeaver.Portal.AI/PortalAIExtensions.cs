using MeshWeaver.AI;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Portal.AI;

/// <summary>
/// Extension methods for adding Portal AI services
/// </summary>
public static class PortalAIExtensions
{
    /// <summary>
    /// Adds Portal AI services including the AgentResolver for graph-based agent resolution.
    /// Agents are now loaded from the graph (MeshNode with nodeType="Agent") instead of DI.
    /// </summary>
    public static IServiceCollection AddPortalAI(this IServiceCollection services)
    {
        // AgentResolver is registered in AddAgentChatFactoryProvider
        // No individual agent registrations needed - agents come from the graph
        return services;
    }
}
