using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.AI.Bedrock;

/// <summary>
/// Extension methods for adding AWS Bedrock AI services
/// </summary>
public static class BedrockExtensions
{
    /// <summary>
    /// Adds AWS Bedrock AI services to the service collection
    /// </summary>
    public static IServiceCollection AddBedrockAI(this IServiceCollection services)
    {
        return services.AddSingleton<IAgentChatFactory, BedrockChatCompletionAgentChatFactory>();
    }
}
