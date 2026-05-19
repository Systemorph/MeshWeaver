using System.Collections.Immutable;
using MeshWeaver.AI;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeshWeaver.AI.AzureOpenAI;

/// <summary>
/// Extension methods for adding Azure OpenAI services. Each provider
/// self-registers its bootstrap profile via
/// <see cref="LanguageModelNodeType.AddLanguageModelCatalogSource{TBuilder}(TBuilder, LanguageModelCatalogSource)"/>
/// — no central registry.
/// </summary>
public static class AzureOpenAIExtensions
{
    /// <summary>
    /// One-call registration of Azure OpenAI — catalog profile + IOptions
    /// binding (<c>AzureOpenAI:</c>) +
    /// <see cref="AzureOpenAIChatClientAgentFactory"/>. Idempotent.
    /// </summary>
    public static TBuilder AddAzureOpenAI<TBuilder>(this TBuilder builder, string configSection = "AzureOpenAI")
        where TBuilder : MeshBuilder
    {
        builder.AddLanguageModelCatalogSource(new LanguageModelCatalogSource(
            SectionName: configSection,
            ProviderName: "AzureOpenAI",
            Order: 3,
            DisplayLabel: "Azure OpenAI",
            DefaultEndpoint: null,
            DefaultModelIds: ImmutableArray<string>.Empty,
            RequiresApiKey: true));
        builder.ConfigureServices(services =>
        {
            services.AddOptions<AzureOpenAIConfiguration>().BindConfiguration(configSection);
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IChatClientFactory, AzureOpenAIChatClientAgentFactory>());
            return services;
        });
        return builder;
    }

    /// <summary>
    /// Adds Azure OpenAI services to the service collection
    /// </summary>
    public static IServiceCollection AddAzureOpenAI(this IServiceCollection services)
    {
        return services.AddSingleton<IChatClientFactory, AzureOpenAIChatClientAgentFactory>();
    }

    /// <summary>
    /// Adds Azure OpenAI services with configuration action.
    /// </summary>
    public static IServiceCollection AddAzureOpenAI(
        this IServiceCollection services,
        Action<AzureOpenAIConfiguration> configure)
    {
        services.Configure(configure);
        return services.AddSingleton<IChatClientFactory, AzureOpenAIChatClientAgentFactory>();
    }
}
