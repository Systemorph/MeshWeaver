using Azure;
using Azure.AI.Inference;
using MeshWeaver.Graph.Configuration;
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
    IOptions<AzureFoundryConfiguration> options,
    ILogger<AzureFoundryChatCompletionAgentChatFactory> logger)
    : ChatCompletionAgentChatFactory(hub)
{
    private readonly AzureFoundryConfiguration configuration = options.Value ?? throw new ArgumentNullException(nameof(options));

    public override string Name => "Azure Foundry";

    public override IReadOnlyList<string> Models => configuration.Models;

    public override int DisplayOrder => configuration.DisplayOrder;

    protected override IChatClient CreateChatClient(AgentConfiguration agentConfig)
    {
        if (string.IsNullOrEmpty(configuration.Endpoint))
            throw new InvalidOperationException("Endpoint is required in AzureFoundryConfiguration");

        if (string.IsNullOrEmpty(configuration.ApiKey))
            throw new InvalidOperationException("ApiKey is required in AzureFoundryConfiguration");

        // Use CurrentModelName if set, fall back to agent's preferred model, otherwise use first configured model
        var modelName = !string.IsNullOrEmpty(CurrentModelName) ? CurrentModelName
            : !string.IsNullOrEmpty(agentConfig.PreferredModel) ? agentConfig.PreferredModel
            : configuration.Models.FirstOrDefault();

        if (string.IsNullOrEmpty(modelName))
            throw new InvalidOperationException("At least one model must be configured in AzureFoundryConfiguration.Models");

        logger.LogInformation(
            "Creating Azure Foundry chat client for agent {AgentName} using model {ModelName}",
            agentConfig.Id, modelName);

        try
        {
            var client = new ChatCompletionsClient(
                new Uri(configuration.Endpoint),
                new AzureKeyCredential(configuration.ApiKey));

            IChatClient chatClient = client.AsIChatClient(modelName);

            logger.LogInformation(
                "Successfully configured Azure Foundry chat client for agent {AgentName} with endpoint {Endpoint} and model {ModelName}",
                agentConfig.Id, configuration.Endpoint, modelName);

            return chatClient;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create Azure Foundry chat client for agent {AgentName}", agentConfig.Id);
            throw new InvalidOperationException(
                $"Failed to create Azure Foundry chat client for agent {agentConfig.Id}: {ex.Message}", ex);
        }
    }
}
