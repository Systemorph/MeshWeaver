using MeshWeaver.AI;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.CreativeCloud.AI;

/// <summary>
/// Extension methods for registering CreativeCloud AI services.
/// </summary>
public static class CreativeCloudAIExtensions
{
    /// <summary>
    /// Adds CreativeCloud AI agents to the service collection.
    /// </summary>
    public static IServiceCollection AddCreativeCloudAI(this IServiceCollection services)
    {
        services.AddSingleton<IAgentDefinition, StoryBreakdownAgent>();
        services.AddSingleton<IAgentDefinition, TranscriptToStoryAgent>();

        return services;
    }
}
