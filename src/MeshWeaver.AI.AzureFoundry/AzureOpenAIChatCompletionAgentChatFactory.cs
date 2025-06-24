using Azure;
using Azure.AI.Inference;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

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
    protected override Microsoft.SemanticKernel.Kernel CreateKernel(IAgentDefinition agentDefinition)
    {
        // Validate configuration
        if (string.IsNullOrEmpty(configuration.Endpoint))
            throw new InvalidOperationException("Azure AI Foundry project endpoint is required in configuration");

        // Get or create the default model name
        var modelName = configuration.Models.FirstOrDefault();
        if (string.IsNullOrEmpty(modelName))
            throw new InvalidOperationException("At least one model must be configured in AzureAIFoundryConfiguration.Models");

        logger.LogInformation("Creating Azure AI Foundry kernel for agent {AgentName} using model {ModelName}",
            agentDefinition.Name, modelName);

        try
        {
            var client = new ChatCompletionsClient(new Uri(configuration.Endpoint), new AzureKeyCredential(configuration.ApiKey!));

            // Create a kernel builder with Azure AI Foundry integration
#pragma warning disable SKEXP0070
            var kernelBuilder = Microsoft.SemanticKernel.Kernel
                .CreateBuilder()
                .AddAzureAIInferenceChatCompletion(
                    modelId: modelName,
                    client)
#pragma warning restore SKEXP0070
                ;

            logger.LogInformation("Successfully configured Azure AI Foundry kernel for agent {AgentName} with endpoint {Endpoint} and model {ModelName}",
                agentDefinition.Name, configuration.Endpoint, modelName);

            return kernelBuilder.Build();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create Azure AI Foundry kernel for agent {AgentName}", agentDefinition.Name);
            throw new InvalidOperationException(
                $"Failed to create Azure AI Foundry kernel for agent {agentDefinition.Name}: {ex.Message}", ex);
        }
    }
}
