using Amazon;
using Amazon.BedrockRuntime;
using Amazon.Runtime;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

namespace MeshWeaver.AI.Bedrock;

/// <summary>
/// Factory for creating agent chats using ChatCompletionAgent with AWS Bedrock.
/// ChatCompletionAgent is used for stateless chat completion scenarios without persistent assistant storage.
/// </summary>
public class BedrockChatCompletionAgentChatFactory(
    IMessageHub hub,
    IEnumerable<IAgentDefinition> agentDefinitions,
    IOptions<BedrockConfiguration> options)
    : ChatCompletionAgentChatFactory(hub, agentDefinitions)
{
    private readonly BedrockConfiguration credentials = options.Value ?? throw new ArgumentNullException(nameof(options));
    private IAmazonBedrockRuntime Runtime { get; } = new AmazonBedrockRuntimeClient(
        new BasicAWSCredentials(options.Value.AccessKey, options.Value.SecretAccessKey),
        new AmazonBedrockRuntimeConfig
        {
            RegionEndpoint = RegionEndpoint.GetBySystemName(options.Value.Region),
            
        });


    protected override Microsoft.SemanticKernel.Kernel CreateKernel(IAgentDefinition agentDefinition)
    {
        // Get the model ID (use first available model)
        var modelId = credentials.Models.FirstOrDefault() ?? throw new InvalidOperationException("At least one Bedrock model must be configured");

        // Create a new kernel for this agent 
        // Note: Bedrock support in Semantic Kernel is still in alpha
        // For now, create a basic kernel that can be extended
#pragma warning disable SKEXP0070
        var kernelBuilder = Microsoft.SemanticKernel.Kernel
            .CreateBuilder()
            .AddBedrockChatCompletionService(modelId,Runtime);
#pragma warning restore SKEXP0070


        return kernelBuilder.Build();
    }
}
