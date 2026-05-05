using Azure;
using Azure.AI.Inference;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.AI.AzureFoundry;

/// <summary>
/// Factory for creating ChatClientAgent instances with Azure AI Foundry services.
/// </summary>
public class AzureFoundryChatClientAgentFactory(
    IMessageHub hub,
    IOptions<AzureFoundryConfiguration> options,
    ILogger<AzureFoundryChatClientAgentFactory> logger)
    : ChatClientAgentFactory(hub)
{
    private readonly AzureFoundryConfiguration configuration = InitAndLog(options, logger);

    private static AzureFoundryConfiguration InitAndLog(IOptions<AzureFoundryConfiguration> options, ILogger logger)
    {
        var config = options.Value ?? throw new ArgumentNullException(nameof(options));
        logger.LogInformation(
            "[AzureFoundryChatClientAgentFactory] Initialized with Endpoint={Endpoint}, ApiKey={HasApiKey}, Models ({ModelCount}): [{Models}]",
            config.Endpoint ?? "(null)",
            !string.IsNullOrEmpty(config.ApiKey) ? "set" : "MISSING",
            config.Models.Length,
            string.Join(", ", config.Models));
        return config;
    }

    public override string Name => "Azure Foundry";

    public override IReadOnlyList<string> Models => configuration.Models;

    public override int Order => configuration.Order;

    /// <summary>
    /// Multi-model gateway. Serves OpenAI-shape names (gpt-*, o*-mini, etc.), Mistral,
    /// DeepSeek, and any other model the deployment exposes through the /models path.
    /// Excludes claude-* (which goes through the dedicated Anthropic endpoint via
    /// <see cref="AzureClaudeChatClientAgentFactory"/>). This catch-all is what makes
    /// agent-declared PreferredModel work without any Models[] enumeration on the
    /// deployment side.
    /// </summary>
    public override bool Supports(string modelName) =>
        !string.IsNullOrEmpty(modelName)
        && !modelName.StartsWith("claude", StringComparison.OrdinalIgnoreCase);

    protected override IChatClient CreateChatClient(AgentConfiguration agentConfig)
    {
        if (string.IsNullOrEmpty(configuration.Endpoint))
            throw new InvalidOperationException("Endpoint is required in AzureFoundryConfiguration");

        if (string.IsNullOrEmpty(configuration.ApiKey))
            throw new InvalidOperationException("ApiKey is required in AzureFoundryConfiguration");

        // Agent's PreferredModel wins; CurrentModelName fills in only when the agent doesn't pin one.
        var modelName = !string.IsNullOrEmpty(agentConfig.PreferredModel) ? agentConfig.PreferredModel
            : !string.IsNullOrEmpty(CurrentModelName) ? CurrentModelName
            : configuration.Models.FirstOrDefault();

        if (string.IsNullOrEmpty(modelName))
            throw new InvalidOperationException("At least one model must be configured in AzureFoundryConfiguration.Models");

        logger.LogInformation(
            "Creating Azure Foundry chat client for agent {AgentName} using model {ModelName}",
            agentConfig.Id, modelName);

        try
        {
            var client = new ChatCompletionsClient(
                new Uri(configuration.Endpoint),
                new AzureKeyCredential(configuration.ApiKey));

            IChatClient chatClient = client.AsIChatClient(modelName);

            logger.LogInformation(
                "Successfully configured Azure Foundry chat client for agent {AgentName} with endpoint {Endpoint} and model {ModelName}",
                agentConfig.Id, configuration.Endpoint, modelName);

            return chatClient;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create Azure Foundry chat client for agent {AgentName}", agentConfig.Id);
            throw new InvalidOperationException(
                $"Failed to create Azure Foundry chat client for agent {agentConfig.Id}: {ex.Message}", ex);
        }
    }
}
