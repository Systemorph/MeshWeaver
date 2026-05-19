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

    public override string Name => "Azure Foundry";

    public override IReadOnlyList<string> Models => configuration.Models;

    public override int Order => configuration.Order;

    /// <summary>
    /// Multi-model gateway. Serves OpenAI-shape names (gpt-*, o*-mini, etc.), Mistral,
    /// DeepSeek, and any other model the deployment exposes through the /models path.
    /// Excludes claude-* (which goes through the dedicated Anthropic endpoint via
    /// <see cref="AzureClaudeChatClientAgentFactory"/>). This catch-all is what makes
    /// agent-declared PreferredModel work without any Models[] enumeration on the
    /// deployment side.
    /// </summary>
    public override bool Supports(string modelName) =>
        !string.IsNullOrEmpty(modelName)
        && !modelName.StartsWith("claude", StringComparison.OrdinalIgnoreCase);

    protected override IChatClient CreateChatClient(AgentConfiguration agentConfig)
    {
        // Agent's PreferredModel wins; CurrentModelName fills in only when the agent doesn't pin one.
        var modelName = !string.IsNullOrEmpty(agentConfig.PreferredModel) ? agentConfig.PreferredModel
            : !string.IsNullOrEmpty(CurrentModelName) ? CurrentModelName
            : configuration.Models.FirstOrDefault();

        if (string.IsNullOrEmpty(modelName))
            throw new InvalidOperationException("At least one model must be configured in AzureFoundryConfiguration.Models");

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
            "[AzureFoundry] Creating chat client agent={AgentName} model={ModelName} endpoint={Endpoint} source={Source} apiKeyFp={ApiKeyFingerprint}",
            agentConfig.Id, modelName, endpoint, source, Fingerprint(apiKey));

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
            logger.LogError(ex, "Failed to create Azure Foundry chat client for agent {AgentName}", agentConfig.Id);
            throw new InvalidOperationException(
                $"Failed to create Azure Foundry chat client for agent {agentConfig.Id}: {ex.Message}", ex);
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
