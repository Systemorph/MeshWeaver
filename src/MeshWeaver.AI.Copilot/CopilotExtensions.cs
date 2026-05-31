using System.Collections.Immutable;
using MeshWeaver.AI;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeshWeaver.AI.Copilot;

/// <summary>
/// Extension methods for adding GitHub Copilot services.
/// </summary>
public static class CopilotExtensions
{
    /// <summary>
    /// Registers the GitHub Copilot provider's catalog profile so its models
    /// surface in the chat picker + Models settings tab — the MeshBuilder
    /// counterpart to <see cref="ClaudeCodeExtensions"/>'s catalog source.
    /// <c>RequiresApiKey: false</c> because Copilot authenticates via GitHub
    /// device-flow OAuth (the "Log in via browser" flow), not a pasted key.
    /// Pair with <see cref="AddCopilot(IServiceCollection, Action{CopilotConfiguration})"/>
    /// which registers the factory + binds <see cref="CopilotConfiguration"/>.
    /// </summary>
    public static TBuilder AddCopilot<TBuilder>(this TBuilder builder)
        where TBuilder : MeshBuilder
    {
        builder.AddLanguageModelCatalogSource(new LanguageModelCatalogSource(
            SectionName: "Copilot",
            ProviderName: "Copilot",
            Order: 6,
            DisplayLabel: "GitHub Copilot",
            DefaultEndpoint: null,
            DefaultModelIds: ImmutableArray<string>.Empty,   // retrieved live from the CLI — never hard-coded
            RequiresApiKey: false));
        return builder;
    }

    /// <summary>
    /// Adds GitHub Copilot services to the service collection.
    /// Configuration should be bound to CopilotConfiguration.
    /// </summary>
    public static IServiceCollection AddCopilot(this IServiceCollection services)
    {
        services.TryAddSingleton<CopilotModelCatalog>();
        return services.AddSingleton<IChatClientFactory, CopilotChatClientAgentFactory>();
    }

    /// <summary>
    /// Adds GitHub Copilot services with configuration action.
    /// </summary>
    public static IServiceCollection AddCopilot(
        this IServiceCollection services,
        Action<CopilotConfiguration> configure)
    {
        services.Configure(configure);
        services.TryAddSingleton<CopilotModelCatalog>();
        return services.AddSingleton<IChatClientFactory, CopilotChatClientAgentFactory>();
    }
}
