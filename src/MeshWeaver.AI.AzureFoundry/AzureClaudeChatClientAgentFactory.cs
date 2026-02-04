using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.AI;

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
    private readonly AzureClaudeConfiguration configuration = options.Value ?? throw new ArgumentNullException(nameof(options));

    public override string Name => "Azure Claude";

    public override IReadOnlyList<string> Models => configuration.Models;

    public override int DisplayOrder => configuration.DisplayOrder;

    protected override IChatClient CreateChatClient(AgentConfiguration agentConfig)
    {
        if (string.IsNullOrEmpty(configuration.Endpoint))
            throw new InvalidOperationException("Endpoint is required in AzureClaudeConfiguration");

        if (string.IsNullOrEmpty(configuration.ApiKey))
            throw new InvalidOperationException("ApiKey is required in AzureClaudeConfiguration");

        // Use CurrentModelName if set, fall back to agent's preferred model, otherwise use first configured model
        var modelName = !string.IsNullOrEmpty(CurrentModelName) ? CurrentModelName
            : !string.IsNullOrEmpty(agentConfig.PreferredModel) ? agentConfig.PreferredModel
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
