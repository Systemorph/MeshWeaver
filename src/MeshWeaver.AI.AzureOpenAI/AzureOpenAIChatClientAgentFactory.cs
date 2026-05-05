using Azure;
using Azure.AI.OpenAI;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.AI.AzureOpenAI;

/// <summary>
/// Factory for creating ChatClientAgent instances with Azure OpenAI.
/// </summary>
public class AzureOpenAIChatClientAgentFactory(
    IMessageHub hub,
    IOptions<AzureOpenAIConfiguration> options,
    ILogger<AzureOpenAIChatClientAgentFactory> logger)
    : ChatClientAgentFactory(hub)
{
    private readonly AzureOpenAIConfiguration credentials = InitAndLog(options, logger);

    private static AzureOpenAIConfiguration InitAndLog(IOptions<AzureOpenAIConfiguration> options, ILogger logger)
    {
        var config = options.Value ?? throw new ArgumentNullException(nameof(options));
        logger.LogInformation(
            "[AzureOpenAIChatClientAgentFactory] Initialized with Endpoint={Endpoint}, ApiKey={HasApiKey}, Models ({ModelCount}): [{Models}]",
            config.Endpoint ?? "(null)",
            !string.IsNullOrEmpty(config.ApiKey) ? "set" : "MISSING",
            config.Models.Length,
            string.Join(", ", config.Models));
        return config;
    }

    public override string Name => "Azure OpenAI";

    public override IReadOnlyList<string> Models => credentials.Models;

    public override int Order => credentials.Order;

    protected override IChatClient CreateChatClient(AgentConfiguration agentConfig)
    {
        // Validate credentials
        if (string.IsNullOrEmpty(credentials.Endpoint))
            throw new InvalidOperationException("Azure OpenAI endpoint URL is required in AI configuration");
        if (string.IsNullOrEmpty(credentials.ApiKey))
            throw new InvalidOperationException("Azure OpenAI API key is required in AI configuration");

        // Agent's PreferredModel wins. CurrentModelName (the globally selected
        // model in the chat dropdown) is only used when the agent doesn't pin a
        // model; first configured model fills in as a last resort. Models
        // declared in agent definitions are the source of truth — see
        // Doc/AI/* — so a globally selected model never overrides an
        // agent-declared one.
        var modelName = !string.IsNullOrEmpty(agentConfig.PreferredModel) ? agentConfig.PreferredModel
            : !string.IsNullOrEmpty(CurrentModelName) ? CurrentModelName
            : credentials.Models.FirstOrDefault();

        if (string.IsNullOrEmpty(modelName))
            throw new InvalidOperationException("No model configured for Azure OpenAI");

        // Create Azure OpenAI client and get chat client
        var azureClient = new AzureOpenAIClient(
            new Uri(credentials.Endpoint),
            new AzureKeyCredential(credentials.ApiKey));

        // Get the chat completion client for the model and convert it to IChatClient
        var openAIChatClient = azureClient.GetChatClient(modelName);

        // Use the AsChatClient extension method to convert OpenAI.Chat.ChatClient to Microsoft.Extensions.AI.IChatClient
        // Note: Do NOT add UseFunctionInvocation() here - ChatClientAgent from Microsoft.Agents.AI
        // handles tool/function invocation internally. Double-wrapping causes streaming issues.
        IChatClient chatClient = openAIChatClient.AsIChatClient();

        return chatClient;
    }
}
