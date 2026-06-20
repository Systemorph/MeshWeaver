using Azure;
using Azure.AI.OpenAI;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.AI.OpenAI;

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
        // Composer selection wins; then the agent's ModelTier; first configured model as a last resort.
        var modelName = !string.IsNullOrEmpty(CurrentModelName) ? CurrentModelName
            : ResolveTierModel(agentConfig) ?? credentials.Models.FirstOrDefault();

        if (string.IsNullOrEmpty(modelName))
            throw new InvalidOperationException("No model configured for Azure OpenAI");

        // Resolver follows ModelDefinition.ProviderRef → ModelProvider node.
        // Falls back to IOptions when no provider node has been configured.
        var resolver = Hub.ServiceProvider.GetRequiredService<ChatClientCredentialResolver>();
        var resolution = resolver.Resolve(modelName);
        var endpoint = resolution.Endpoint ?? credentials.Endpoint;
        var apiKey = resolution.ApiKey ?? credentials.ApiKey;
        var source = resolution.Endpoint != null || resolution.ApiKey != null
            ? resolution.Source : "IOptions";

        if (string.IsNullOrEmpty(endpoint))
            throw new InvalidOperationException(
                $"Endpoint is missing for model '{modelName}'. Configure a ModelProvider node (Model/AzureOpenAI) or set AzureOpenAI:Endpoint in config.");
        if (string.IsNullOrEmpty(apiKey))
            throw new InvalidOperationException(
                $"ApiKey is missing for model '{modelName}'. Configure a ModelProvider node (Model/AzureOpenAI) or set AzureOpenAI:ApiKey in config.");

        logger.LogInformation(
            "[AzureOpenAI] Creating chat client agent={AgentName} model={ModelName} endpoint={Endpoint} source={Source} apiKeyFp={ApiKeyFingerprint}",
            agentConfig.Id, modelName, endpoint, source, Fingerprint(apiKey));

        // Create Azure OpenAI client and get chat client
        var azureClient = new AzureOpenAIClient(
            new Uri(endpoint),
            new AzureKeyCredential(apiKey));

        // Get the chat completion client for the model and convert it to IChatClient
        var openAIChatClient = azureClient.GetChatClient(modelName);

        // Use the AsChatClient extension method to convert OpenAI.Chat.ChatClient to Microsoft.Extensions.AI.IChatClient
        // Note: Do NOT add UseFunctionInvocation() here - ChatClientAgent from Microsoft.Agents.AI
        // handles tool/function invocation internally. Double-wrapping causes streaming issues.
        IChatClient chatClient = openAIChatClient.AsIChatClient();

        return chatClient;
    }

    /// <summary>
    /// 8-char SHA-256-hex prefix of <paramref name="value"/>. Used in logs to
    /// disambiguate "which key was actually used" without exposing the key.
    /// </summary>
    private static string Fingerprint(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "(empty)";
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }
}
