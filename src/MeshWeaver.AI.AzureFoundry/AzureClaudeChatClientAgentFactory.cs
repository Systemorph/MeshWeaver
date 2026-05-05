using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.AI.AzureFoundry;

/// <summary>
/// Factory for creating ChatClientAgent instances with Azure AI Foundry Claude/Anthropic services.
/// </summary>
public class AzureClaudeChatClientAgentFactory(
    IMessageHub hub,
    IOptions<AzureClaudeConfiguration> options,
    ILogger<AzureClaudeChatClientAgentFactory> logger)
    : ChatClientAgentFactory(hub)
{
    private readonly AzureClaudeConfiguration configuration = InitAndLog(options, logger);

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
    /// Claude factory: serves any model name starting with "claude" (case-insensitive).
    /// Covers claude-sonnet-4-6, claude-opus-4-7, claude-haiku-4-5, etc. without requiring
    /// the deployed Models[] to enumerate every variant — agents can pin any Claude model
    /// declared in their PreferredModel and routing finds this factory.
    /// </summary>
    public override bool Supports(string modelName) =>
        !string.IsNullOrEmpty(modelName)
        && modelName.StartsWith("claude", StringComparison.OrdinalIgnoreCase);

    protected override IChatClient CreateChatClient(AgentConfiguration agentConfig)
    {
        if (string.IsNullOrEmpty(configuration.Endpoint))
            throw new InvalidOperationException("Endpoint is required in AzureClaudeConfiguration");

        if (string.IsNullOrEmpty(configuration.ApiKey))
            throw new InvalidOperationException("ApiKey is required in AzureClaudeConfiguration");

        // Agent's PreferredModel wins; CurrentModelName fills in only when the agent doesn't pin one.
        var modelName = !string.IsNullOrEmpty(agentConfig.PreferredModel) ? agentConfig.PreferredModel
            : !string.IsNullOrEmpty(CurrentModelName) ? CurrentModelName
            : configuration.Models.FirstOrDefault();

        if (string.IsNullOrEmpty(modelName))
            throw new InvalidOperationException("At least one model must be configured in AzureClaudeConfiguration.Models");

        logger.LogDebug(
            "Creating Azure Claude chat client for agent {AgentName} using model {ModelName} at endpoint {Endpoint}",
            agentConfig.Id, modelName, configuration.Endpoint);

        try
        {
            var chatClient = new AzureClaudeChatClient(
                endpoint: configuration.Endpoint,
                apiKey: configuration.ApiKey,
                modelId: modelName);

            logger.LogDebug(
                "Successfully configured Azure Claude chat client for agent {AgentName}",
                agentConfig.Id);

            return chatClient;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create Azure Claude chat client for agent {AgentName}", agentConfig.Id);
            throw new InvalidOperationException(
                $"Failed to create Azure Claude chat client for agent {agentConfig.Id}: {ex.Message}", ex);
        }
    }
}
