using MeshWeaver.Messaging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

namespace MeshWeaver.AI.AzureFoundry;

/// <summary>
/// Factory for creating agent chats using ChatCompletionAgent with Azure OpenAI.
/// ChatCompletionAgent is used for stateless chat completion scenarios without persistent assistant storage.
/// </summary>
public class AzureAIChatCompletionAgentChatFactory(
    IMessageHub hub,
    IEnumerable<IAgentDefinition> agentDefinitions,
    IOptions<AICredentialsConfiguration> options)
    : ChatCompletionAgentChatFactory(hub, agentDefinitions)
{
    private readonly AICredentialsConfiguration _credentials = options.Value ?? throw new ArgumentNullException(nameof(options));

    protected override async Task<Microsoft.SemanticKernel.Kernel> CreateKernelAsync(IAgentDefinition agentDefinition)
    {
        // Validate credentials
        if (string.IsNullOrEmpty(_credentials.Url))
            throw new InvalidOperationException("Azure OpenAI endpoint URL is required in AI configuration");
        if (string.IsNullOrEmpty(_credentials.ApiKey))
            throw new InvalidOperationException("Azure OpenAI API key is required in AI configuration");

        // Create a new kernel for this agent with Azure OpenAI chat completion
        var kernelBuilder = Microsoft.SemanticKernel.Kernel.CreateBuilder()
            .AddAzureOpenAIChatCompletion(
                deploymentName: _credentials.Models.First(),
                endpoint: _credentials.Url,
                apiKey: _credentials.ApiKey);

        return await Task.FromResult(kernelBuilder.Build());
    }
}
