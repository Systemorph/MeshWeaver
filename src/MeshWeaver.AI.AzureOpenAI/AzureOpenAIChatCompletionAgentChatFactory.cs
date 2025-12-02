using Azure;
using Azure.AI.OpenAI;
using Azure.Core;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace MeshWeaver.AI.AzureOpenAI;

/// <summary>
/// Factory for creating agent chats using ChatClientAgent with Azure OpenAI.
/// ChatClientAgent is used for stateless chat completion scenarios without persistent assistant storage.
/// </summary>
public class AzureOpenAIChatCompletionAgentChatFactory(
    IMessageHub hub,
    IEnumerable<IAgentDefinition> agentDefinitions,
    IOptions<AzureOpenAIConfiguration> options)
    : ChatCompletionAgentChatFactory(hub, agentDefinitions)
{
    private readonly AzureOpenAIConfiguration credentials = options.Value ?? throw new ArgumentNullException(nameof(options));

    public override string Name => "Azure OpenAI";

    public override IReadOnlyList<string> Models => credentials.Models;

    protected override IChatClient CreateChatClient(IAgentDefinition agentDefinition)
    {
        // Validate credentials
        if (string.IsNullOrEmpty(credentials.Endpoint))
            throw new InvalidOperationException("Azure OpenAI endpoint URL is required in AI configuration");
        if (string.IsNullOrEmpty(credentials.ApiKey))
            throw new InvalidOperationException("Azure OpenAI API key is required in AI configuration");

        // Use CurrentModelName if set, otherwise fall back to first model
        var modelName = !string.IsNullOrEmpty(CurrentModelName) ? CurrentModelName : credentials.Models.FirstOrDefault();
        if (string.IsNullOrEmpty(modelName))
            throw new InvalidOperationException("No model configured for Azure OpenAI");

        // Create Azure OpenAI client and get chat client
        var azureClient = new AzureOpenAIClient(
            new Uri(credentials.Endpoint),
            new AzureKeyCredential(credentials.ApiKey));

        // Get the chat completion client for the model and convert it to IChatClient
        var openAIChatClient = azureClient.GetChatClient(modelName);

        // Use the AsChatClient extension method to convert OpenAI.Chat.ChatClient to Microsoft.Extensions.AI.IChatClient
        // Wrap with ChatClientBuilder and enable function invocation for automatic tool calling
        IChatClient chatClient = new ChatClientBuilder(openAIChatClient.AsIChatClient())
            .UseFunctionInvocation()
            .Build();

        return chatClient;
    }
}
