using System.Collections.Immutable;
using MeshWeaver.AI;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeshWeaver.AI.OpenAI;

/// <summary>
/// Builder extensions for OpenAI-wire-protocol providers: direct OpenAI
/// (<c>api.openai.com</c>) and the generic <c>OpenAICompatible</c> custom-URL
/// provider (OpenRouter, Groq, Together, a local vLLM, …). Both are served by
/// <see cref="OpenAIChatClientAgentFactory"/>; they differ only in the catalog
/// profile they self-register via
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

    /// <summary>
    /// One-call registration of the generic <b>OpenAI-compatible</b> provider —
    /// the catalog "type" a user picks in Settings → Language Models when bringing
    /// any OpenAI-wire endpoint by URL + key (OpenRouter, Groq, Together, a local
    /// vLLM, …). There is no system-default endpoint or model list: the user
    /// supplies the base URL and fetches the model list live, and each saved
    /// provider stores its own endpoint on its <c>ModelProvider</c> node, so
    /// several distinct gateways coexist. Reuses
    /// <see cref="OpenAIChatClientAgentFactory"/> (it already owns the
    /// <c>OpenAICompatible</c> provider stamp); the factory registration is
    /// idempotent with <see cref="AddOpenAI{TBuilder}"/>.
    /// </summary>
    public static TBuilder AddOpenAICompatible<TBuilder>(this TBuilder builder, string configSection = "OpenAICompatible")
        where TBuilder : MeshBuilder
    {
        builder.AddLanguageModelCatalogSource(new LanguageModelCatalogSource(
            SectionName: configSection,
            ProviderName: "OpenAICompatible",
            Order: 5,
            DisplayLabel: "OpenAI-compatible (custom URL)",
            DefaultEndpoint: null,   // user supplies the base URL (e.g. https://openrouter.ai/api/v1)
            DefaultModelIds: ImmutableArray<string>.Empty,
            RequiresApiKey: true));
        builder.ConfigureServices(services =>
        {
            // No BindConfiguration: there is no system-default OpenAICompatible
            // section — endpoint + key always come from the user's ModelProvider
            // node. AddOptions alone guarantees IOptions<OpenAIConfiguration>
            // resolves (empty) when AddOpenAI wasn't also called.
            services.AddOptions<OpenAIConfiguration>();
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IChatClientFactory, OpenAIChatClientAgentFactory>());
            return services;
        });
        return builder;
    }
}
