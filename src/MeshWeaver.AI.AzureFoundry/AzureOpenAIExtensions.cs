using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.AI.AzureFoundry;

/// <summary>
/// Extension methods for adding Azure AI Foundry services
/// </summary>
public static class AzureFoundryExtensions
{
    /// <summary>
    /// Adds Azure AI Foundry services to the service collection
    /// </summary>
    /// <param name="services">The service collection to add services to</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddAzureAIFoundry(this IServiceCollection services)
    {
        return services.AddSingleton<IAgentChatFactory, AzureAIFoundryChatCompletionAgentChatFactory>();
    }
}
