using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.AI.AzureFoundry;

/// <summary>
/// Extension methods for adding Azure Foundry AI services
/// </summary>
public static class AzureFoundryExtensions
{
    /// <summary>
    /// Adds Azure AI Foundry services to the service collection
    /// </summary>
    public static IServiceCollection AddAzureOpenAI(this IServiceCollection services)
    {
        return services.AddSingleton<IAgentChatFactory, AzureOpenAIChatCompletionAgentChatFactory>();
    }
}
