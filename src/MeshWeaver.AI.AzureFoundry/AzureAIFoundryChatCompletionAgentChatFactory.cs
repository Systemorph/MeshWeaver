using Azure;
using Azure.AI.Inference;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.AI;

namespace MeshWeaver.AI.AzureFoundry;

/// <summary>
/// Factory for creating agent chats using Azure AI Foundry services.
/// This implementation uses Azure AI Foundry's unified project endpoint to access models and agents.
/// Note: This is a transitional implementation until full Azure AI Foundry Agent Service support is available.
/// For production scenarios, consider using MeshWeaver.AI.AzureOpenAI for direct Azure OpenAI integration.
/// </summary>
public class AzureAIFoundryChatCompletionAgentChatFactory(
    IMessageHub hub,
    IEnumerable<IAgentDefinition> agentDefinitions,
    IOptions<AzureAIFoundryConfiguration> options,
    ILogger<AzureAIFoundryChatCompletionAgentChatFactory> logger)
    : ChatCompletionAgentChatFactory(hub, agentDefinitions)
{
    private readonly AzureAIFoundryConfiguration configuration = options.Value ?? throw new ArgumentNullException(nameof(options));

    protected override IChatClient CreateChatClient(IAgentDefinition agentDefinition)
    {
        // Validate configuration
        if (string.IsNullOrEmpty(configuration.Endpoint))
            throw new InvalidOperationException("Azure AI Foundry project endpoint is required in configuration");

        // Get or create the default model name
        var modelName = configuration.Models.FirstOrDefault();
        if (string.IsNullOrEmpty(modelName))
            throw new InvalidOperationException("At least one model must be configured in AzureAIFoundryConfiguration.Models");

        logger.LogInformation("Creating Azure AI Foundry chat client for agent {AgentName} using model {ModelName}",
            agentDefinition.Name, modelName);

        try
        {
            var client = new ChatCompletionsClient(new Uri(configuration.Endpoint), new AzureKeyCredential(configuration.ApiKey!));

            // Use the AsChatClient extension method to convert ChatCompletionsClient to Microsoft.Extensions.AI.IChatClient
            IChatClient chatClient = client.AsIChatClient(modelName);

            logger.LogInformation("Successfully configured Azure AI Foundry chat client for agent {AgentName} with endpoint {Endpoint} and model {ModelName}",
                agentDefinition.Name, configuration.Endpoint, modelName);

            return chatClient;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create Azure AI Foundry chat client for agent {AgentName}", agentDefinition.Name);
            throw new InvalidOperationException(
                $"Failed to create Azure AI Foundry chat client for agent {agentDefinition.Name}: {ex.Message}", ex);
        }
    }
}
