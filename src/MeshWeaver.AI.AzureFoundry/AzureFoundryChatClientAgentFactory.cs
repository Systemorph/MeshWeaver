using Azure;
using Azure.AI.Inference;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.AI.AzureFoundry;

/// <summary>
/// Factory for creating ChatClientAgent instances with Azure AI Foundry services.
/// </summary>
public class AzureFoundryChatClientAgentFactory(
    IMessageHub hub,
    IOptions<AzureFoundryConfiguration> options,
    ILogger<AzureFoundryChatClientAgentFactory> logger)
    : ChatClientAgentFactory(hub)
{
    private readonly AzureFoundryConfiguration configuration = InitAndLog(options, logger);

    private static AzureFoundryConfiguration InitAndLog(IOptions<AzureFoundryConfiguration> options, ILogger logger)
    {
        var config = options.Value ?? throw new ArgumentNullException(nameof(options));
        logger.LogInformation(
            "[AzureFoundryChatClientAgentFactory] Initialized with Endpoint={Endpoint}, ApiKey={HasApiKey}, Models ({ModelCount}): [{Models}]",
            config.Endpoint ?? "(null)",
            !string.IsNullOrEmpty(config.ApiKey) ? "set" : "MISSING",
            config.Models.Length,
            string.Join(", ", config.Models));
        return config;
    }

    /// <summary>Display name of this factory, surfaced in the model/provider listings.</summary>
    public override string Name => "Azure Foundry";

    /// <summary>The model ids this factory advertises as available through the Azure AI Foundry deployment.</summary>
    public override IReadOnlyList<string> Models => configuration.Models;

    /// <summary>Selection priority among factories; lower values are preferred when several factories support the same model.</summary>
    public override int Order => configuration.Order;

    /// <summary>
    /// Multi-model gateway. Serves OpenAI-shape names (gpt-*, o*-mini, etc.), Mistral,
    /// DeepSeek, and any other model the deployment exposes through the /models path.
    /// Excludes claude-* (which goes through the dedicated Anthropic endpoint via
    /// <see cref="AzureClaudeChatClientAgentFactory"/>). This catch-all serves the
    /// chat-selected model without any Models[] enumeration on the deployment side —
    /// but ONLY for models whose declared provider is AzureFoundry or unknown. A model
    /// the catalog declares under ANOTHER provider (e.g.
    /// <c>Provider/OpenAICompatible/qwen-small</c> → Ollama) must route to that
    /// provider's factory; the unconditional catch-all at Order 0 used to hijack it,
    /// build an Azure ChatCompletionsClient against the foreign endpoint, and fail every
    /// round with 403 (the e2e chat-round breaker, 2026-07-02). Same gating rule the
    /// resolver documents on <see cref="ChatClientCredentialResolver.GetProviderForModel"/>.
    /// </summary>
    public override bool Supports(string modelName)
    {
        if (string.IsNullOrEmpty(modelName))
            return false;
        // The picker persists EITHER a bare id ("claude-…") OR a full LanguageModel node path
        // ("Provider/Anthropic/claude-…") — test the LAST segment so a path-shaped Claude name
        // is excluded even when the resolver snapshot is cold and can't name the provider yet.
        var lastSlash = modelName.LastIndexOf('/');
        var bareId = lastSlash >= 0 ? modelName[(lastSlash + 1)..] : modelName;
        if (bareId.StartsWith("claude", StringComparison.OrdinalIgnoreCase))
            return false;
        var provider = Hub.ServiceProvider.GetService<ChatClientCredentialResolver>()
            ?.GetProviderForModel(modelName);
        return provider is null
               || string.Equals(provider, "AzureFoundry", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds an <see cref="IChatClient"/> for the model selected for the given
    /// agent. Resolves the model name (composer selection, then the agent's
    /// ModelTier, then the first configured model), then resolves the endpoint and
    /// API key via the <see cref="ChatClientCredentialResolver"/> with an IOptions
    /// fallback, and wraps an Azure <c>ChatCompletionsClient</c> as a chat client.
    /// </summary>
    /// <param name="agentConfig">Configuration of the agent the client is created for; supplies the model tier and identifies the agent in logs and errors.</param>
    /// <returns>A chat client targeting the resolved Azure AI Foundry model endpoint.</returns>
    protected override IChatClient CreateChatClient(AgentConfiguration agentConfig)
    {
        // Composer selection wins; then the agent's ModelTier; first configured model as a last resort.
        var modelName = !string.IsNullOrEmpty(CurrentModelName) ? CurrentModelName
            : ResolveTierModel(agentConfig) ?? configuration.Models.FirstOrDefault();

        if (string.IsNullOrEmpty(modelName))
            throw new InvalidOperationException("At least one model must be configured in AzureFoundryConfiguration.Models");

        return CreateChatClientForModel(modelName);
    }

    /// <inheritdoc />
    protected override IChatClient CreateChatClientForModel(string modelName)
    {
        // Resolver follows ModelDefinition.ProviderRef → ModelProvider node
        // for Endpoint + ApiKey. Falls back to IOptions configuration when
        // no provider node is present (legacy single-tenant deployments).
        var resolver = Hub.ServiceProvider.GetRequiredService<ChatClientCredentialResolver>();
        var resolution = resolver.Resolve(modelName);
        var endpoint = resolution.Endpoint ?? configuration.Endpoint;
        var apiKey = resolution.ApiKey ?? configuration.ApiKey;
        var source = resolution.Endpoint != null || resolution.ApiKey != null
            ? resolution.Source : "IOptions";

        if (string.IsNullOrEmpty(endpoint))
            throw new InvalidOperationException(
                $"Endpoint is missing for model '{modelName}'. Configure a ModelProvider node (Model/AzureFoundry) or set AzureFoundry:Endpoint in config.");

        if (string.IsNullOrEmpty(apiKey))
            throw new InvalidOperationException(
                $"ApiKey is missing for model '{modelName}'. Configure a ModelProvider node (Model/AzureFoundry) or set AzureFoundry:ApiKey in config.");

        logger.LogInformation(
            "[AzureFoundry] Creating chat client model={ModelName} endpoint={Endpoint} source={Source} apiKeyFp={ApiKeyFingerprint}",
            modelName, endpoint, source, Fingerprint(apiKey));

        try
        {
            var client = new ChatCompletionsClient(
                new Uri(endpoint),
                new AzureKeyCredential(apiKey));

            IChatClient chatClient = client.AsIChatClient(modelName);

            return chatClient;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create Azure Foundry chat client for model {ModelName}", modelName);
            throw new InvalidOperationException(
                $"Failed to create Azure Foundry chat client for model {modelName}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 8-char SHA-256-hex prefix of <paramref name="value"/>. Used in logs to
    /// disambiguate "which key was actually used" without exposing the key.
    /// </summary>
    private static string Fingerprint(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "(empty)";
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }
}
