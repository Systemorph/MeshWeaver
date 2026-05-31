using System.Collections.Immutable;
using MeshWeaver.AI;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeshWeaver.AI.AzureOpenAI;

/// <summary>
/// Builder extension for direct OpenAI (<c>api.openai.com</c>), the
/// bring-your-own personal-key counterpart to <see cref="AzureOpenAIExtensions"/>.
/// Self-registers its catalog profile via
/// <see cref="LanguageModelNodeType.AddLanguageModelCatalogSource{TBuilder}(TBuilder, LanguageModelCatalogSource)"/>
/// — no central registry.
/// </summary>
public static class OpenAIExtensions
{
    /// <summary>
    /// One-call registration of direct OpenAI — catalog profile + IOptions
    /// binding (<c>OpenAI:</c>) + <see cref="OpenAIChatClientAgentFactory"/>.
    /// Idempotent.
    /// </summary>
    public static TBuilder AddOpenAI<TBuilder>(this TBuilder builder, string configSection = "OpenAI")
        where TBuilder : MeshBuilder
    {
        builder.AddLanguageModelCatalogSource(new LanguageModelCatalogSource(
            SectionName: configSection,
            ProviderName: "OpenAI",
            Order: 4,
            DisplayLabel: "OpenAI",
            DefaultEndpoint: null,   // SDK default: https://api.openai.com
            DefaultModelIds: ImmutableArray.Create("gpt-4o", "gpt-4o-mini"),
            RequiresApiKey: true));
        builder.ConfigureServices(services =>
        {
            services.AddOptions<OpenAIConfiguration>().BindConfiguration(configSection);
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IChatClientFactory, OpenAIChatClientAgentFactory>());
            return services;
        });
        return builder;
    }
}
