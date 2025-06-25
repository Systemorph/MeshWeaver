using MeshWeaver.AI;
using MeshWeaver.Documentation.AI;
using MeshWeaver.Northwind.AI;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Portal.AI;

/// <summary>
/// Extension methods for adding Portal AI services
/// </summary>
public static class PortalAIExtensions
{
    /// <summary>
    /// Adds Portal AI services including the MeshNavigator agent and Azure AI Foundry integration
    /// </summary>
    public static IServiceCollection AddPortalAI(this IServiceCollection services)
    {
        return services
            .AddSingleton<IAgentDefinition, MeshNavigator>()
            .AddNorthwindAI()
            .AddDocumentationAI();
    }
}
