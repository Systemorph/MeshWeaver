using System.Collections.Immutable;
using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI.Plugins;

/// <summary>
/// Represents a delegation result that can be expanded in the UI.
/// </summary>
public record DelegationResult
{
    /// <summary>
    /// The agent that was delegated to.
    /// </summary>
    public required string AgentName { get; init; }

    /// <summary>
    /// The task that was delegated.
    /// </summary>
    public required string Task { get; init; }

    /// <summary>
    /// The result from the delegated agent.
    /// </summary>
    public required string Result { get; init; }

    /// <summary>
    /// Whether the delegation was successful.
    /// </summary>
    public bool Success { get; init; } = true;

    /// <summary>
    /// The thread ID used for the delegation (for isolated context).
    /// </summary>
    public string? ThreadId { get; init; }
}

/// <summary>
/// Information about an agent available for delegation.
/// </summary>
/// <param name="AgentPath">Path or ID of the agent</param>
/// <param name="Description">When to delegate to this agent</param>
public record DelegationInfo(string AgentPath, string Description);

/// <summary>
/// Creates delegation tools for agents that support isolated context per delegation.
/// Each delegation creates a new thread for the target agent, and the result
/// is returned to the parent agent as a tool result.
/// </summary>
public static class DelegationTool
{
    /// <summary>
    /// Creates a delegation tool that delegates to a specific agent with an isolated thread.
    /// The delegation is visible in the UI as a tool invocation.
    /// </summary>
    /// <param name="targetAgentName">Name of the agent to delegate to</param>
    /// <param name="targetAgentDescription">Description of when to use this agent</param>
    /// <param name="executeAsync">Function to execute the delegation</param>
    /// <param name="logger">Optional logger</param>
    /// <returns>An AITool for delegation</returns>
    public static AITool CreateDelegationTool(
        string targetAgentName,
        string targetAgentDescription,
        Func<string, CancellationToken, Task<DelegationResult>> executeAsync,
        ILogger? logger = null)
    {
        async Task<string> DelegateToAgent(
            [Description("The task or instructions to send to the specialized agent. Be specific about what you need.")] string task,
            CancellationToken cancellationToken)
        {
            logger?.LogInformation("Delegating to {TargetAgent}: {Task}", targetAgentName, task);

            try
            {
                var result = await executeAsync(task, cancellationToken);

                if (result.Success)
                {
                    logger?.LogInformation("Delegation to {TargetAgent} completed successfully", targetAgentName);
                    return result.Result;
                }
                else
                {
                    logger?.LogWarning("Delegation to {TargetAgent} failed: {Result}", targetAgentName, result.Result);
                    return $"Delegation to {targetAgentName} failed: {result.Result}";
                }
            }
            catch (OperationCanceledException)
            {
                logger?.LogInformation("Delegation to {TargetAgent} was cancelled", targetAgentName);
                throw;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Error during delegation to {TargetAgent}", targetAgentName);
                return $"Error delegating to {targetAgentName}: {ex.Message}";
            }
        }

        var description = $"""
            {targetAgentDescription}

            This tool delegates the task to the {targetAgentName} agent, which has specialized capabilities.
            The agent will execute the task in its own isolated context and return the result.
            Wait for the result before continuing with your response.
            """;

        return AIFunctionFactory.Create(
            DelegateToAgent,
            name: $"delegate_to_{targetAgentName}",
            description: description);
    }

    /// <summary>
    /// Creates a unified delegation tool that can delegate to any available agent.
    /// Each delegation uses an isolated thread for the target agent.
    /// </summary>
    /// <param name="currentAgent">The current agent's configuration</param>
    /// <param name="hierarchyAgents">All agents in the namespace hierarchy</param>
    /// <param name="executeAsync">Function to execute delegations</param>
    /// <param name="logger">Optional logger</param>
    /// <returns>An AITool for unified delegation</returns>
    public static AITool CreateUnifiedDelegationTool(
        AgentConfiguration currentAgent,
        IReadOnlyList<AgentConfiguration> hierarchyAgents,
        Func<string, string, string?, CancellationToken, Task<DelegationResult>> executeAsync,
        ILogger? logger = null)
    {
        var delegationInfo = ImmutableList<DelegationInfo>.Empty;

        // Add explicit delegations from current agent
        if (currentAgent.Delegations != null)
        {
            foreach (var d in currentAgent.Delegations)
            {
                delegationInfo = delegationInfo.Add(new DelegationInfo(
                    d.AgentPath,
                    d.Instructions ?? "Specialized agent for this task"));
            }
        }

        // Add hierarchy agents for escalation (excluding current agent)
        foreach (var agent in hierarchyAgents.Where(a => a.Id != currentAgent.Id))
        {
            if (!delegationInfo.Any(d => d.AgentPath == agent.Id || d.AgentPath.EndsWith($"/{agent.Id}")))
            {
                delegationInfo = delegationInfo.Add(new DelegationInfo(
                    agent.Id,
                    agent.Description ?? $"Agent {agent.Id}"));
            }
        }

        var agentsJson = JsonSerializer.Serialize(delegationInfo, new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        async Task<string> Delegate(
            [Description("The name of the agent to delegate to. Use the agentPath from the available agents.")] string agentName,
            [Description("The task or instructions for the delegated agent. Be specific about what you need.")] string task,
            [Description("Optional: the node path to use as context for this delegation (e.g., 'OrgA/my-doc'). When omitted, inherits the parent context. Set explicitly when delegating parallel work on different documents.")] string? context = null,
            CancellationToken cancellationToken = default)
        {
            logger?.LogInformation("Delegating to {AgentName}: {Task}, context={Context}", agentName, task, context ?? "(inherited)");

            try
            {
                var result = await executeAsync(agentName, task, context, cancellationToken);

                if (result.Success)
                {
                    logger?.LogInformation("Delegation to {AgentName} completed successfully", agentName);
                    return result.Result;
                }
                else
                {
                    logger?.LogWarning("Delegation to {AgentName} failed: {Result}", agentName, result.Result);
                    return $"Delegation to {agentName} failed: {result.Result}";
                }
            }
            catch (OperationCanceledException)
            {
                logger?.LogInformation("Delegation to {AgentName} was cancelled", agentName);
                throw;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Error during delegation to {AgentName}", agentName);
                return $"Error delegating to {agentName}: {ex.Message}";
            }
        }

        var description = $"""
            Delegate to a specialized agent when the request matches their expertise.
            Each delegation runs in an isolated context - the agent won't see previous conversation history.
            Wait for the result before continuing with your response.

            When delegating parallel work on different documents, set the 'context' parameter to the
            specific node path for each delegation. This ensures each agent sees the correct document.
            When omitted, the parent's context is inherited.

            Available agents:
            {agentsJson}

            Choose the most appropriate agent based on the user's request.
            """;

        return AIFunctionFactory.Create(
            Delegate,
            name: "delegate_to_agent",
            description: description);
    }
}
