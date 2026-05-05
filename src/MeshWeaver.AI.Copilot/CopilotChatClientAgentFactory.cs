using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.AI.Copilot;

/// <summary>
/// Factory for creating ChatClientAgent instances with GitHub Copilot SDK.
/// </summary>
public class CopilotChatClientAgentFactory(
    IMessageHub hub,
    IOptions<CopilotConfiguration> options,
    ILogger<CopilotChatClientAgentFactory> logger)
    : ChatClientAgentFactory(hub)
{
    private readonly CopilotConfiguration configuration = options.Value ?? new CopilotConfiguration();

    public override string Name => "GitHub Copilot";

    public override IReadOnlyList<string> Models => configuration.Models;

    public override int Order => configuration.Order;

    protected override IChatClient CreateChatClient(AgentConfiguration agentConfig)
    {
        // Agent's PreferredModel wins; CurrentModelName fills in only when the agent doesn't pin one.
        var modelName = !string.IsNullOrEmpty(agentConfig.PreferredModel) ? agentConfig.PreferredModel
            : !string.IsNullOrEmpty(CurrentModelName) ? CurrentModelName
            : configuration.Models.FirstOrDefault();

        logger.LogInformation(
            "Creating GitHub Copilot chat client for agent {AgentName} using model {ModelName}",
            agentConfig.Id, modelName);

        try
        {
            var clientLogger = Hub.ServiceProvider.GetService(typeof(ILogger<CopilotChatClient>)) as ILogger<CopilotChatClient>;
            var chatClient = new CopilotChatClient(configuration, modelName, clientLogger);

            logger.LogInformation(
                "Successfully configured GitHub Copilot chat client for agent {AgentName}",
                agentConfig.Id);

            return chatClient;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create GitHub Copilot chat client for agent {AgentName}", agentConfig.Id);
            throw new InvalidOperationException(
                $"Failed to create GitHub Copilot chat client for agent {agentConfig.Id}: {ex.Message}", ex);
        }
    }
}
