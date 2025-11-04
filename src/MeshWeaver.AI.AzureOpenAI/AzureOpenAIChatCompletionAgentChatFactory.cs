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

    protected override IChatClient CreateChatClient(IAgentDefinition agentDefinition)
    {
        // Validate credentials
        if (string.IsNullOrEmpty(credentials.Url))
            throw new InvalidOperationException("Azure OpenAI endpoint URL is required in AI configuration");
        if (string.IsNullOrEmpty(credentials.ApiKey))
            throw new InvalidOperationException("Azure OpenAI API key is required in AI configuration");

        // Create Azure OpenAI client and get chat client
        var azureClient = new AzureOpenAIClient(
            new Uri(credentials.Url),
            new AzureKeyCredential(credentials.ApiKey));

        // Get the chat completion client for the model and convert it to IChatClient
        var openAIChatClient = azureClient.GetChatClient(credentials.Models.First());

        // Use the AsChatClient extension method to convert OpenAI.Chat.ChatClient to Microsoft.Extensions.AI.IChatClient
        IChatClient chatClient = openAIChatClient.AsIChatClient();

        return chatClient;
    }
}
