using MeshWeaver.AI;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Insurance.AI;

/// <summary>
/// Extension methods for registering Insurance AI services.
/// </summary>
public static class InsuranceAIExtensions
{
    /// <summary>
    /// Adds Insurance AI agents to the service collection.
    /// </summary>
    public static IServiceCollection AddInsuranceAI(this IServiceCollection services)
    {
        services.AddSingleton<IAgentDefinition, InsuranceAgent>();
        services.AddSingleton<IAgentDefinition, RiskImportAgent>();
        services.AddSingleton<IAgentDefinition, SlipImportAgent>();

        return services;
    }
}
