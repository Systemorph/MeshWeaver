using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI.Plugins;

/// <summary>
/// Information about an agent available for handoff.
/// </summary>
/// <param name="AgentPath">Path or ID of the agent</param>
/// <param name="Description">When to hand off to this agent</param>
public record HandoffInfo(string AgentPath, string Description);

/// <summary>
/// Creates handoff tools for agents that support transferring control to another agent.
/// Unlike delegation (isolated context, result returned), a handoff transfers the conversation
/// entirely to the target agent on the shared thread. The source agent stops.
/// </summary>
public static class HandoffTool
{
    /// <summary>
    /// Creates a unified handoff tool that can hand off to any available agent.
    /// The handoff sets a pending request and returns immediately, telling the LLM to stop.
    /// </summary>
    /// <param name="currentAgent">The current agent's configuration</param>
    /// <param name="hierarchyAgents">All agents in the namespace hierarchy</param>
    /// <param name="requestHandoff">Callback to set the pending handoff on AgentChatClient</param>
    /// <param name="logger">Optional logger</param>
    /// <returns>An AITool for handoff</returns>
    public static AITool CreateUnifiedHandoffTool(
        AgentConfiguration currentAgent,
        IReadOnlyList<AgentConfiguration> hierarchyAgents,
        Action<HandoffRequest> requestHandoff,
        ILogger? logger = null)
    {
        var handoffInfo = new List<HandoffInfo>();

        // Add explicit handoffs from current agent
        if (currentAgent.Handoffs != null)
        {
            foreach (var h in currentAgent.Handoffs)
            {
                handoffInfo.Add(new HandoffInfo(
                    h.AgentPath,
                    h.Instructions ?? "Specialized agent for this task"));
            }
        }

        var agentsJson = JsonSerializer.Serialize(handoffInfo, new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        string Handoff(
            [Description("The name of the agent to hand off to. Use the agentPath from the available agents.")] string agentName,
            [Description("A message describing the context and what the target agent should do. The target agent will see the full conversation history.")] string message)
        {
            logger?.LogInformation("Handoff from {Source} to {Target}: {Message}",
                currentAgent.Id, agentName, message);

            requestHandoff(new HandoffRequest(currentAgent.Id, agentName, message));

            return "Handoff initiated. Do not continue responding.";
        }

        var description = $"""
            Transfer control of the conversation to another agent.
            Unlike delegation, the target agent takes over completely on the shared thread
            with full conversation history. You will stop responding after the handoff.

            Use handoff when the target agent should directly interact with the user,
            rather than returning results to you.

            Available agents for handoff:
            {agentsJson}

            Choose the most appropriate agent based on the user's request.
            """;

        return AIFunctionFactory.Create(
            Handoff,
            name: "handoff_to_agent",
            description: description);
    }
}
