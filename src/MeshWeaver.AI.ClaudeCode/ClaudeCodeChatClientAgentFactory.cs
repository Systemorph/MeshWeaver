using MeshWeaver.AI;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.AI.ClaudeCode;

/// <summary>
/// Factory for creating ChatClientAgent instances with Claude Code (Claude Agent SDK).
/// Requires Claude Code CLI >= 2.0.0 installed.
/// </summary>
public class ClaudeCodeChatClientAgentFactory(
    IMessageHub hub,
    IOptions<ClaudeCodeConfiguration> options,
    ILogger<ClaudeCodeChatClientAgentFactory> logger)
    : ChatClientAgentFactory(hub)
{
    private readonly ClaudeCodeConfiguration configuration = options.Value ?? new ClaudeCodeConfiguration();

    public override string Name => "Claude Code";

    public override IReadOnlyList<string> Models => configuration.Models;

    public override int DisplayOrder => configuration.DisplayOrder;

    protected override IChatClient CreateChatClient(AgentConfiguration agentConfig)
    {
        // Use CurrentModelName if set, fall back to agent's preferred model, otherwise use first configured model
        var modelName = !string.IsNullOrEmpty(CurrentModelName) ? CurrentModelName
            : !string.IsNullOrEmpty(agentConfig.PreferredModel) ? agentConfig.PreferredModel
            : configuration.Models.FirstOrDefault();

        logger.LogInformation(
            "Creating Claude Code chat client for agent {AgentName} using model {ModelName}",
            agentConfig.Id, modelName);

        try
        {
            var clientLogger = Hub.ServiceProvider.GetService(typeof(ILogger<ClaudeCodeChatClient>)) as ILogger<ClaudeCodeChatClient>;
            var chatClient = new ClaudeCodeChatClient(configuration, modelName, clientLogger);

            logger.LogInformation(
                "Successfully configured Claude Code chat client for agent {AgentName}",
                agentConfig.Id);

            return chatClient;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create Claude Code chat client for agent {AgentName}", agentConfig.Id);
            throw new InvalidOperationException(
                $"Failed to create Claude Code chat client for agent {agentConfig.Id}: {ex.Message}", ex);
        }
    }
}
