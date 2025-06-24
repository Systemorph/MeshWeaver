using MeshWeaver.Messaging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

namespace MeshWeaver.AI.AzureOpenAI;

/// <summary>
/// Factory for creating agent chats using ChatCompletionAgent with Azure OpenAI.
/// ChatCompletionAgent is used for stateless chat completion scenarios without persistent assistant storage.
/// </summary>
public class AzureOpenAIChatCompletionAgentChatFactory(
    IMessageHub hub,
    IEnumerable<IAgentDefinition> agentDefinitions,
    IOptions<AzureOpenAIConfiguration> options)
    : ChatCompletionAgentChatFactory(hub, agentDefinitions)
{
    private readonly AzureOpenAIConfiguration credentials = options.Value ?? throw new ArgumentNullException(nameof(options));

    protected override Microsoft.SemanticKernel.Kernel CreateKernel(IAgentDefinition agentDefinition)
    {
        // Validate credentials
        if (string.IsNullOrEmpty(credentials.Url))
            throw new InvalidOperationException("Azure OpenAI endpoint URL is required in AI configuration");
        if (string.IsNullOrEmpty(credentials.ApiKey))
            throw new InvalidOperationException("Azure OpenAI API key is required in AI configuration");

        // Create a new kernel for this agent with Azure OpenAI chat completion
        var kernelBuilder = Microsoft.SemanticKernel.Kernel.CreateBuilder()
            .AddAzureOpenAIChatCompletion(
                deploymentName: credentials.Models.First(),
                endpoint: credentials.Url,
                apiKey: credentials.ApiKey);

        return kernelBuilder.Build();
    }
}
