using System.Collections.Immutable;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeshWeaver.AI.AzureFoundry;

/// <summary>
/// Extension methods for adding Azure AI Foundry services. Each provider
/// self-registers its bootstrap profile via
/// <see cref="LanguageModelNodeType.AddLanguageModelCatalogSource{TBuilder}(TBuilder, LanguageModelCatalogSource)"/>
/// — there is no central registry. Callers opt in to each provider they
/// actually need.
/// </summary>
public static class AzureFoundryExtensions
{
    /// <summary>
    /// One-call registration of Anthropic — catalog profile + IOptions
    /// binding (<c>Anthropic:</c>) + the
    /// <see cref="AzureClaudeChatClientAgentFactory"/>. The same factory
    /// serves direct <c>api.anthropic.com</c> AND Azure-hosted Anthropic;
    /// the actual endpoint comes from the user's <c>ModelProvider</c> node
    /// (or IOptions fallback for system defaults). Idempotent.
    /// </summary>
    public static TBuilder AddAnthropic<TBuilder>(this TBuilder builder, string configSection = "Anthropic")
        where TBuilder : MeshBuilder
    {
        builder.AddLanguageModelCatalogSource(new LanguageModelCatalogSource(
            SectionName: configSection,
            ProviderName: "Anthropic",
            Order: 1,
            DisplayLabel: "Anthropic",
            DefaultEndpoint: "https://api.anthropic.com/v1/messages",
            DefaultModelIds: ImmutableArray.Create(
                "claude-opus-4-8",
                "claude-sonnet-4-6",
                "claude-haiku-4-5-20251001"),
            RequiresApiKey: true));
        builder.ConfigureServices(services =>
        {
            services.AddOptions<AzureClaudeConfiguration>().BindConfiguration(configSection);
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IChatClientFactory, AzureClaudeChatClientAgentFactory>());
            return services;
        });
        return builder;
    }

    /// <summary>
    /// One-call registration of Azure Foundry multi-model gateway —
    /// catalog profile + IOptions binding (<c>AzureFoundry:</c>) +
    /// <see cref="AzureFoundryChatClientAgentFactory"/>. Idempotent.
    /// </summary>
    public static TBuilder AddAzureFoundry<TBuilder>(this TBuilder builder, string configSection = "AzureFoundry")
        where TBuilder : MeshBuilder
    {
        builder.AddLanguageModelCatalogSource(new LanguageModelCatalogSource(
            SectionName: configSection,
            ProviderName: "AzureFoundry",
            Order: 2,
            DisplayLabel: "Azure Foundry",
            DefaultEndpoint: null,
            DefaultModelIds: ImmutableArray<string>.Empty,
            RequiresApiKey: true));
        builder.ConfigureServices(services =>
        {
            services.AddOptions<AzureFoundryConfiguration>().BindConfiguration(configSection);
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IChatClientFactory, AzureFoundryChatClientAgentFactory>());
            return services;
        });
        return builder;
    }

    /// <summary>
    /// Adds Azure AI Foundry Claude services.
    /// Configuration should be bound to AzureClaudeConfiguration.
    /// </summary>
    public static IServiceCollection AddAzureFoundryClaude(this IServiceCollection services)
    {
        return services.AddSingleton<IChatClientFactory, AzureClaudeChatClientAgentFactory>();
    }

    /// <summary>
    /// Adds Azure AI Foundry Claude services with configuration action.
    /// </summary>
    public static IServiceCollection AddAzureFoundryClaude(
        this IServiceCollection services,
        Action<AzureClaudeConfiguration> configure)
    {
        services.Configure(configure);
        return services.AddSingleton<IChatClientFactory, AzureClaudeChatClientAgentFactory>();
    }

    /// <summary>
    /// Adds Azure AI Foundry services.
    /// Configuration should be bound to AzureFoundryConfiguration.
    /// </summary>
    public static IServiceCollection AddAzureFoundry(this IServiceCollection services)
    {
        return services.AddSingleton<IChatClientFactory, AzureFoundryChatClientAgentFactory>();
    }

    /// <summary>
    /// Adds Azure AI Foundry services with configuration action.
    /// </summary>
    public static IServiceCollection AddAzureFoundry(
        this IServiceCollection services,
        Action<AzureFoundryConfiguration> configure)
    {
        services.Configure(configure);
        return services.AddSingleton<IChatClientFactory, AzureFoundryChatClientAgentFactory>();
    }

    /// <summary>
    /// Adds Azure AI Foundry persistent agent services.
    /// Persistent agents maintain server-side conversation history via persistent threads.
    /// </summary>
    public static IServiceCollection AddAzureFoundryPersistent(this IServiceCollection services)
        => services.AddSingleton<IChatClientFactory, AzureFoundryPersistentAgentFactory>();

    /// <summary>
    /// Adds Azure AI Foundry persistent agent services with configuration action.
    /// </summary>
    public static IServiceCollection AddAzureFoundryPersistent(
        this IServiceCollection services,
        Action<AzureFoundryPersistentConfiguration> configure)
    {
        services.Configure(configure);
        return services.AddSingleton<IChatClientFactory, AzureFoundryPersistentAgentFactory>();
    }
}
