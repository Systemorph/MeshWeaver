using Azure;
using Azure.AI.Inference;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.AI;

namespace MeshWeaver.AI.AzureFoundry;

/// <summary>
/// Factory for creating agent chats using Azure AI Foundry services.
/// </summary>
public class AzureFoundryChatCompletionAgentChatFactory(
    IMessageHub hub,
    IEnumerable<IAgentDefinition> agentDefinitions,
    IOptions<AzureFoundryConfiguration> options,
    ILogger<AzureFoundryChatCompletionAgentChatFactory> logger)
    : ChatCompletionAgentChatFactory(hub, agentDefinitions)
{
    private readonly AzureFoundryConfiguration configuration = options.Value ?? throw new ArgumentNullException(nameof(options));

    protected override IChatClient CreateChatClient(IAgentDefinition agentDefinition)
    {
        if (string.IsNullOrEmpty(configuration.Endpoint))
            throw new InvalidOperationException("Endpoint is required in AzureFoundryConfiguration");

        if (string.IsNullOrEmpty(configuration.ApiKey))
            throw new InvalidOperationException("ApiKey is required in AzureFoundryConfiguration");

        var modelName = configuration.Models.FirstOrDefault();
        if (string.IsNullOrEmpty(modelName))
            throw new InvalidOperationException("At least one model must be configured in AzureFoundryConfiguration.Models");

        logger.LogInformation(
            "Creating Azure Foundry chat client for agent {AgentName} using model {ModelName}",
            agentDefinition.Name, modelName);

        try
        {
            var client = new ChatCompletionsClient(
                new Uri(configuration.Endpoint),
                new AzureKeyCredential(configuration.ApiKey));

            IChatClient chatClient = client.AsIChatClient(modelName);

            logger.LogInformation(
                "Successfully configured Azure Foundry chat client for agent {AgentName} with endpoint {Endpoint} and model {ModelName}",
                agentDefinition.Name, configuration.Endpoint, modelName);

            return chatClient;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create Azure Foundry chat client for agent {AgentName}", agentDefinition.Name);
            throw new InvalidOperationException(
                $"Failed to create Azure Foundry chat client for agent {agentDefinition.Name}: {ex.Message}", ex);
        }
    }
}
