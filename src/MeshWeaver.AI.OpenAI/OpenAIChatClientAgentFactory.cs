using System.ClientModel;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;

namespace MeshWeaver.AI.OpenAI;

/// <summary>
/// Factory for any provider that speaks the <b>OpenAI wire protocol</b> —
/// direct OpenAI (<c>api.openai.com</c>) AND OpenAI-compatible gateways
/// (OpenRouter, Groq, Together, a local vLLM, …) configured with a custom base
/// URL under the generic <c>OpenAICompatible</c> provider. Mirrors
/// <see cref="AzureOpenAIChatClientAgentFactory"/> but builds a plain
/// <see cref="OpenAIClient"/> pointed at the resolved endpoint. Credentials +
/// endpoint resolve from the selected model's <c>ModelProvider</c> node via
/// <see cref="ChatClientCredentialResolver"/> (following each model's
/// <c>ProviderRef</c>, so two custom gateways coexist), falling back to IOptions
/// (<c>OpenAI:</c>) for a system default.
/// </summary>
public class OpenAIChatClientAgentFactory(
    IMessageHub hub,
    IOptions<OpenAIConfiguration> options,
    ILogger<OpenAIChatClientAgentFactory> logger)
    : ChatClientAgentFactory(hub)
{
    private readonly OpenAIConfiguration credentials = options.Value ?? new OpenAIConfiguration();

    /// <summary>Display name of this provider factory ("OpenAI").</summary>
    public override string Name => "OpenAI";

    /// <summary>Model ids this factory serves, as configured in <c>OpenAIConfiguration</c>.</summary>
    public override IReadOnlyList<string> Models => credentials.Models;

    /// <summary>Selection priority among factories; lower values are preferred. Sourced from configuration.</summary>
    public override int Order => credentials.Order;

    /// <summary>
    /// Provider stamps this factory owns. Any model whose <c>ModelProvider</c>
    /// declares one of these routes here: <c>OpenAI</c> (direct api.openai.com)
    /// and <c>OpenAICompatible</c> (the generic custom-URL provider — OpenRouter,
    /// Groq, Together, vLLM, …). The endpoint for the latter comes from the
    /// model's resolved provider node, so the same factory serves any number of
    /// distinct gateways.
    /// </summary>
    private static readonly string[] OwnedProviders = ["OpenAI", "OpenAICompatible", "OpenRouter"];

    /// <summary>
    /// Routes a model here when its <c>ModelProvider</c> declares a provider in
    /// <see cref="OwnedProviders"/> — so a <c>gpt-*</c> id owned by a direct
    /// OpenAI provider, or any model id owned by an OpenAI-compatible gateway,
    /// lands here while an Azure-OpenAI-owned id stays with the Azure factory.
    /// Accepts BOTH the bare model id and the full LanguageModel node path (the
    /// composer's persisted form) — the resolver canonicalises paths.
    /// ALSO routes here when the model is listed in an owned provider's
    /// CONFIG section (<c>OpenAICompatible:Models</c> etc. — the exact section
    /// <c>BuiltInLanguageModelProvider</c> seeds the catalog from): the config
    /// seed must resolve a factory even before the mesh catalog snapshot is
    /// warm, otherwise a freshly booted deployment whose only provider is
    /// config-seeded (e.g. <c>OpenAICompatible__Models__0=qwen-small</c>)
    /// fails every agent build. Additive over the base (Models-list) match,
    /// so it never narrows existing behaviour.
    /// </summary>
    public override bool Supports(string modelName)
    {
        if (string.IsNullOrEmpty(modelName)) return false;
        var provider = Hub.ServiceProvider.GetService<ChatClientCredentialResolver>()
            ?.GetProviderForModel(modelName);
        return (provider != null && OwnedProviders.Contains(provider, StringComparer.OrdinalIgnoreCase))
            || FindConfiguredCatalogSource(modelName) is not null
            || base.Supports(modelName);
    }

    /// <summary>
    /// Finds the owned catalog source (OpenAI / OpenAICompatible / OpenRouter) whose CONFIG
    /// section lists <paramref name="modelName"/> in <c>{Section}:Models</c>, and returns that
    /// section's driver config. This mirrors <c>BuiltInLanguageModelProvider</c>'s seeding read
    /// EXACTLY (same section, same keys, same DefaultEndpoint fallback) — one source of truth —
    /// so a config-seeded model resolves this factory and its endpoint/key WITHOUT depending on
    /// the mesh snapshot being warm. Returns null when no owned section lists the model.
    /// </summary>
    private (string Section, string? Endpoint, string? ApiKey)? FindConfiguredCatalogSource(string modelName)
    {
        var sources = Hub.ServiceProvider.GetService<LanguageModelCatalogOptions>();
        var configuration = Hub.ServiceProvider.GetService<IConfiguration>();
        if (sources is null || configuration is null) return null;

        foreach (var source in sources.Sources)
        {
            if (!OwnedProviders.Contains(source.ProviderName, StringComparer.OrdinalIgnoreCase))
                continue;
            string[]? models;
            try
            {
                models = configuration.GetSection($"{source.SectionName}:Models").Get<string[]>();
            }
            catch
            {
                continue; // malformed section — same skip as the catalog seeder.
            }
            if (models is null
                || !models.Any(m => string.Equals(m, modelName, StringComparison.OrdinalIgnoreCase)))
                continue;
            return (source.SectionName,
                configuration[$"{source.SectionName}:Endpoint"] ?? source.DefaultEndpoint,
                configuration[$"{source.SectionName}:ApiKey"]);
        }
        return null;
    }

    /// <summary>
    /// Builds an <see cref="IChatClient"/> for OpenAI or an OpenAI-compatible gateway. Picks the model
    /// (composer selection, then the agent's model tier, then the first configured model), resolves the
    /// API key and optional base URL via <c>ChatClientCredentialResolver</c> (falling back to configured
    /// <c>IOptions</c> values; a null endpoint uses the SDK default <c>api.openai.com</c>), and returns
    /// a chat client over that model.
    /// </summary>
    /// <param name="agentConfig">Agent configuration used to resolve the model tier and for log context.</param>
    /// <returns>A chat client targeting the resolved model on the resolved endpoint.</returns>
    /// <exception cref="InvalidOperationException">No model is configured, or the API key cannot be resolved.</exception>
    protected override IChatClient CreateChatClient(AgentConfiguration agentConfig)
    {
        // Composer selection wins; then the agent's ModelTier; first configured model as a last resort.
        var modelName = !string.IsNullOrEmpty(CurrentModelName) ? CurrentModelName
            : ResolveTierModel(agentConfig) ?? credentials.Models.FirstOrDefault();

        if (string.IsNullOrEmpty(modelName))
            throw new InvalidOperationException("No model configured for OpenAI");

        var resolver = Hub.ServiceProvider.GetRequiredService<ChatClientCredentialResolver>();
        // The selection may arrive as the full LanguageModel node PATH (the composer's
        // persisted form) — canonicalise to the bare wire id the endpoint expects.
        modelName = resolver.ResolveModelId(modelName) ?? modelName;
        var resolution = resolver.Resolve(modelName);
        // Credential chain: provider node (resolver) → the owned catalog source's CONFIG
        // section (the seed BuiltInLanguageModelProvider reads — works before the mesh
        // snapshot is warm) → legacy IOptions binding (OpenAI: section).
        var configured = FindConfiguredCatalogSource(modelName);
        var endpoint = resolution.Endpoint ?? configured?.Endpoint ?? credentials.Endpoint;   // null → SDK default api.openai.com
        var apiKey = resolution.ApiKey ?? configured?.ApiKey ?? credentials.ApiKey;
        var source = resolution.Endpoint != null || resolution.ApiKey != null ? resolution.Source
            : configured is not null ? $"config:{configured.Value.Section}"
            : "IOptions";

        if (string.IsNullOrEmpty(apiKey))
            throw new InvalidOperationException(
                $"ApiKey is missing for model '{modelName}'. Configure a ModelProvider node (Provider 'OpenAI') or set OpenAI:ApiKey in config.");

        logger.LogInformation(
            "[OpenAI] Creating chat client agent={AgentName} model={ModelName} endpoint={Endpoint} source={Source} apiKeyFp={ApiKeyFingerprint}",
            agentConfig.Id, modelName, endpoint ?? "(default api.openai.com)", source, Fingerprint(apiKey));

        var clientOptions = new OpenAIClientOptions();
        if (!string.IsNullOrEmpty(endpoint))
            clientOptions.Endpoint = new Uri(endpoint);

        var client = new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions);
        return client.GetChatClient(modelName).AsIChatClient();
    }

    /// <summary>8-char SHA-256-hex prefix — logs which key was used, never the key.</summary>
    private static string Fingerprint(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "(empty)";
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }
}
