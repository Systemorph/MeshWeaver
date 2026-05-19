using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.AI.AzureFoundry;

/// <summary>
/// Factory for creating ChatClientAgent instances with Azure AI Foundry Claude/Anthropic services.
///
/// <para>Driver config (Endpoint + ApiKey) source-of-truth precedence:
/// (1) the selected model's <see cref="ModelDefinition"/> on its MeshNode —
///     <see cref="BuiltInLanguageModelProvider"/> stamps the built-ins from
///     the <c>Anthropic</c> config section, but user-authored Model nodes
///     can override per-model;
/// (2) <see cref="AzureClaudeConfiguration"/> (legacy IOptions binding) as
///     fallback when the model node is missing those fields.</para>
/// </summary>
public class AzureClaudeChatClientAgentFactory(
    IMessageHub hub,
    IOptions<AzureClaudeConfiguration> options,
    ILogger<AzureClaudeChatClientAgentFactory> logger)
    : ChatClientAgentFactory(hub)
{
    private readonly AzureClaudeConfiguration configuration = InitAndLog(options, logger);

    private ChatClientCredentialResolver Resolver =>
        Hub.ServiceProvider.GetRequiredService<ChatClientCredentialResolver>();

    private static AzureClaudeConfiguration InitAndLog(IOptions<AzureClaudeConfiguration> options, ILogger logger)
    {
        var config = options.Value ?? throw new ArgumentNullException(nameof(options));
        logger.LogInformation(
            "[AzureClaudeChatClientAgentFactory] Initialized with Endpoint={Endpoint}, ApiKey={HasApiKey}, Models ({ModelCount}): [{Models}]",
            config.Endpoint ?? "(null)",
            !string.IsNullOrEmpty(config.ApiKey) ? "set" : "MISSING",
            config.Models.Length,
            string.Join(", ", config.Models));
        return config;
    }

    public override string Name => "Azure Claude";

    public override IReadOnlyList<string> Models => configuration.Models;

    public override int Order => configuration.Order;

    /// <summary>
    /// Claude factory: serves any model name starting with "claude" (case-insensitive),
    /// regardless of whether the deployment is direct <c>api.anthropic.com</c>
    /// or Azure-hosted Anthropic. Both use the same Messages-API wire protocol;
    /// the endpoint (and therefore the route taken) is resolved at
    /// <see cref="CreateChatClient"/> time from the model's
    /// <c>ModelProvider</c> node via <see cref="ChatClientCredentialResolver"/>.
    /// </summary>
    public override bool Supports(string modelName) =>
        !string.IsNullOrEmpty(modelName)
        && modelName.StartsWith("claude", StringComparison.OrdinalIgnoreCase);

    protected override IChatClient CreateChatClient(AgentConfiguration agentConfig)
    {
        // Agent's PreferredModel wins (resolved from ModelTier in
        // ChatClientAgentFactory.CreateAgent). The chat picker should
        // auto-follow the selected agent's PreferredModel — see
        // ThreadChatView's agent-change handler. CurrentModelName is the
        // fallback when an agent doesn't pin a model.
        var modelName = !string.IsNullOrEmpty(agentConfig.PreferredModel) ? agentConfig.PreferredModel
            : !string.IsNullOrEmpty(CurrentModelName) ? CurrentModelName
            : configuration.Models.FirstOrDefault();

        if (string.IsNullOrEmpty(modelName))
            throw new InvalidOperationException(
                $"No model selected for agent {agentConfig.Id}. Set the agent's PreferredModel or pick one in the chat dropdown.");

        // Driver config: resolver walks parent ModelProvider → root ModelProvider
        // → legacy ModelDefinition fields. Fall back to IOptions if the resolver
        // returns Missing.
        var resolution = Resolver.Resolve(modelName);
        var endpoint = resolution.Endpoint ?? configuration.Endpoint;
        var apiKey = resolution.ApiKey ?? configuration.ApiKey;
        var source = resolution.Endpoint != null || resolution.ApiKey != null
            ? resolution.Source
            : "IOptions";

        if (string.IsNullOrEmpty(endpoint))
            throw new InvalidOperationException(
                $"Endpoint is missing for model '{modelName}'. Configure a ModelProvider node (e.g. Model/Anthropic) or set Anthropic:Endpoint in config.");

        if (string.IsNullOrEmpty(apiKey))
            throw new InvalidOperationException(
                $"ApiKey is missing for model '{modelName}'. Configure a ModelProvider node (e.g. Model/Anthropic) or set Anthropic:ApiKey in config.");

        logger.LogInformation(
            "[AzureClaude] Creating chat client agent={AgentName} model={ModelName} endpoint={Endpoint} source={Source} apiKeyFp={ApiKeyFingerprint}",
            agentConfig.Id, modelName, endpoint, source, Fingerprint(apiKey));

        try
        {
            return new AzureClaudeChatClient(endpoint: endpoint, apiKey: apiKey, modelId: modelName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create Azure Claude chat client for agent {AgentName}", agentConfig.Id);
            throw new InvalidOperationException(
                $"Failed to create Azure Claude chat client for agent {agentConfig.Id}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 8-char SHA-256-hex prefix of <paramref name="value"/>. Used in logs to
    /// disambiguate "which key was actually used" without ever logging the
    /// key itself. Two requests using the same key produce the same
    /// fingerprint; a stale Model-node-stamped key vs a fresh config key
    /// shows up as a fingerprint mismatch.
    /// </summary>
    private static string Fingerprint(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "(empty)";
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }
}
