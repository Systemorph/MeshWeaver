using MeshWeaver.AI;
using MeshWeaver.Mesh;
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

    public override int DisplayOrder => configuration.DisplayOrder;

    protected override IChatClient CreateChatClient(AgentConfiguration agentConfig)
    {
        // Use CurrentModelName if set, fall back to agent's preferred model, otherwise use first configured model
        var modelName = !string.IsNullOrEmpty(CurrentModelName) ? CurrentModelName
            : !string.IsNullOrEmpty(agentConfig.PreferredModel) ? agentConfig.PreferredModel
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
