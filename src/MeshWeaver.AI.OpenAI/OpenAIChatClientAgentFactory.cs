using System.ClientModel;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
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

    public override string Name => "OpenAI";

    public override IReadOnlyList<string> Models => credentials.Models;

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
    /// Additive over the base (Models-list) match, so it never narrows existing
    /// behaviour.
    /// </summary>
    public override bool Supports(string modelName)
    {
        if (string.IsNullOrEmpty(modelName)) return false;
        var provider = Hub.ServiceProvider.GetService<ChatClientCredentialResolver>()
            ?.GetProviderForModel(modelName);
        return (provider != null && OwnedProviders.Contains(provider, StringComparer.OrdinalIgnoreCase))
            || base.Supports(modelName);
    }

    protected override IChatClient CreateChatClient(AgentConfiguration agentConfig)
    {
        // Composer selection wins; then the agent's ModelTier; first configured model as a last resort.
        var modelName = !string.IsNullOrEmpty(CurrentModelName) ? CurrentModelName
            : ResolveTierModel(agentConfig) ?? credentials.Models.FirstOrDefault();

        if (string.IsNullOrEmpty(modelName))
            throw new InvalidOperationException("No model configured for OpenAI");

        var resolver = Hub.ServiceProvider.GetRequiredService<ChatClientCredentialResolver>();
        var resolution = resolver.Resolve(modelName);
        var endpoint = resolution.Endpoint ?? credentials.Endpoint;   // null → SDK default api.openai.com
        var apiKey = resolution.ApiKey ?? credentials.ApiKey;
        var source = resolution.Endpoint != null || resolution.ApiKey != null
            ? resolution.Source : "IOptions";

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
