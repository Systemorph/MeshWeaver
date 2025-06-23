using MeshWeaver.AI;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Northwind.AI;

/// <summary>
/// Extension methods for adding Northwind AI services
/// </summary>
public static class NorthwindAIExtensions
{
    /// <summary>
    /// Adds Northwind AI services including the NorthwindAgent
    /// </summary>
    public static IServiceCollection AddNorthwindAI(this IServiceCollection services)
    {
        return services
            .AddSingleton<IAgentDefinition, NorthwindAgent>();
    }
}
