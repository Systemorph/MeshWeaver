using System.Collections.Concurrent;
using Azure;
using Azure.AI.OpenAI;
using MeshWeaver.Application.Styles;
using MeshWeaver.Domain;
using MeshWeaver.Messaging;
using MeshWeaver.Reinsurance.AI;
using MeshWeaver.Utils;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;

namespace MeshWeaver.AI;

public interface IChatService
{
    IChatClient Get(string model = null);
    ChatOptions GetOptions(IMessageHub hub, string uri, string selectedModel = null);

    public ProgressMessage GetProgressMessage(FunctionCallContent content);
    string SystemPrompt { get; }

}

public class ChatService : IChatService
{
    private readonly AIConfiguration configuration;
    private readonly OpenAIClient client;

    public ChatService(IOptions<AICredentialsConfiguration> options, AIConfiguration configuration)
    {
        this.configuration = configuration;
        credentialsConfiguration = options.Value;
        if (credentialsConfiguration is null)
            throw new InvalidOperationException(
                "Need to configure an AI section in the credentialsConfiguration with Url and ApiKey");
        var url = credentialsConfiguration.Url;
        if (url is null)
            throw new InvalidOperationException(
                "Need to configure an Url within the AI section. Example: https://models.inference.ai.azure.com");
        var apiKey = credentialsConfiguration.ApiKey;

        if (apiKey is null)
            throw new InvalidOperationException("Need to configure an ApiKey inside the AI credentialsConfiguration section");
        var openAIOptions = new OpenAIClientOptions()
        {
            Endpoint = new Uri(url)
        };

        var credential = new AzureKeyCredential(apiKey);
        client = new AzureOpenAIClient(new Uri(url), credential);
    }

    private readonly ConcurrentDictionary<string, IChatClient> clients = new();
    private readonly AICredentialsConfiguration credentialsConfiguration;

    public IChatClient Get(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
            model = credentialsConfiguration.Models.First();
        return clients.GetOrAdd(model, _ => new ChatClientBuilder(client.GetChatClient(model).AsIChatClient())
            .UseFunctionInvocation()
            .Build());
    }

    public ProgressMessage GetProgressMessage(FunctionCallContent content)
    {
        return content switch
        {
            { Name: "Update" } => new ProgressMessage()
            {
                Icon = FluentIcons.ClipboardTextEdit(IconSize.Size20),
                Message = "Editing pricing..."
            },
            { Name: var name } when name.StartsWith("Get") => new ProgressMessage()
            {
                Icon = FluentIcons.Search(IconSize.Size20),
                Message = $"Retrieving {name.Substring(3).Wordify()}..."
            },
            _ => new ProgressMessage()
            {
                Icon = FluentIcons.ChatSettings(IconSize.Size20),
                Message = $"{content.Name.Wordify()} ..."
            }
        };
    }

    public string SystemPrompt 
    => configuration.SystemPrompt;

    public ChatOptions GetOptions(IMessageHub hub, string uri, string selectedModel)
    {
        var options = new ChatOptions();
        configuration.EnrichOptions(options, new(hub, uri, selectedModel));
        return options;
    }

}
