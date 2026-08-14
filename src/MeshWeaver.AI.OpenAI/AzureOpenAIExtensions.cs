using System.Collections.Immutable;
using MeshWeaver.AI;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeshWeaver.AI.OpenAI;

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
        builder.ConfigureServices(services => services.AddAzureOpenAI(configSection));
        return builder;
    }

    /// <summary>
    /// Adds Azure OpenAI services with configuration action.
    /// </summary>
    public static IServiceCollection AddAzureOpenAI(
        this IServiceCollection services,
        Action<AzureOpenAIConfiguration> configure)
    {
        services.AddAzureOpenAI();
        // POST-configure: the caller's explicit values must win over the config binding the
        // parameterless overload registers (review finding — Configure-before-Bind let the
        // bound section clobber the action's values).
        services.PostConfigure(configure);
        return services;
    }

    /// <summary>
    /// The one collection-level registration (catalog source + options + factory) — the form a
    /// boot-loaded pack carries; the builder overload delegates here. TryAddEnumerable keeps it
    /// idempotent (the legacy bare AddSingleton form double-registered the factory).
    /// </summary>
    public static IServiceCollection AddAzureOpenAI(this IServiceCollection services, string configSection = "AzureOpenAI")
    {
        services.AddLanguageModelCatalogSource(new LanguageModelCatalogSource(
            SectionName: configSection, ProviderName: "AzureOpenAI", Order: 3,
            DisplayLabel: "Azure OpenAI", DefaultEndpoint: null,
            DefaultModelIds: ImmutableArray<string>.Empty, RequiresApiKey: true));
        services.AddOptions<AzureOpenAIConfiguration>().BindConfiguration(configSection);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IChatClientFactory, AzureOpenAIChatClientAgentFactory>());
        return services;
    }
}
