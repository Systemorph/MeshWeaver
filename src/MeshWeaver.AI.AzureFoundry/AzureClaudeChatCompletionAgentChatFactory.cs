using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.AI;

namespace MeshWeaver.AI.AzureFoundry;

/// <summary>
/// Factory for creating agent chats using Azure AI Foundry Claude/Anthropic services.
/// </summary>
public class AzureClaudeChatCompletionAgentChatFactory(
    IMessageHub hub,
    IEnumerable<IAgentDefinition> agentDefinitions,
    IOptions<AzureClaudeConfiguration> options,
    ILogger<AzureClaudeChatCompletionAgentChatFactory> logger)
    : ChatCompletionAgentChatFactory(hub, agentDefinitions)
{
    private readonly AzureClaudeConfiguration configuration = options.Value ?? throw new ArgumentNullException(nameof(options));

    protected override IChatClient CreateChatClient(IAgentDefinition agentDefinition)
    {
        if (string.IsNullOrEmpty(configuration.Endpoint))
            throw new InvalidOperationException("Endpoint is required in AzureClaudeConfiguration");

        if (string.IsNullOrEmpty(configuration.ApiKey))
            throw new InvalidOperationException("ApiKey is required in AzureClaudeConfiguration");

        var modelName = configuration.Models.FirstOrDefault();
        if (string.IsNullOrEmpty(modelName))
            throw new InvalidOperationException("At least one model must be configured in AzureClaudeConfiguration.Models");

        logger.LogInformation(
            "Creating Azure Claude chat client for agent {AgentName} using model {ModelName} at endpoint {Endpoint}",
            agentDefinition.Name, modelName, configuration.Endpoint);

        try
        {
            var chatClient = new AzureClaudeChatClient(
                endpoint: configuration.Endpoint,
                apiKey: configuration.ApiKey,
                modelId: modelName);

            logger.LogInformation(
                "Successfully configured Azure Claude chat client for agent {AgentName}",
                agentDefinition.Name);

            return chatClient;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create Azure Claude chat client for agent {AgentName}", agentDefinition.Name);
            throw new InvalidOperationException(
                $"Failed to create Azure Claude chat client for agent {agentDefinition.Name}: {ex.Message}", ex);
        }
    }
}
