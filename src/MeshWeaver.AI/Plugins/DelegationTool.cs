using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI.Plugins;

/// <summary>
/// Result record preserved for tests + <see cref="ThreadExecution.ExtractToolResult"/>.
/// No longer used as the delegation tool return shape — the tool now yields
/// <see cref="IAsyncEnumerable{string}"/> chunks directly.
/// </summary>
public record DelegationResult
{
    public required string AgentName { get; init; }
    public required string Task { get; init; }
    public required string Result { get; init; }
    public bool Success { get; init; } = true;
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
///
/// The tool signature is <see cref="IAsyncEnumerable{string}"/> so that the sub-thread's
/// streaming text flows back as incremental chunks. Microsoft.Extensions.AI aggregates
/// the yielded chunks as the tool result; meanwhile, a side-channel delta push keeps the
/// parent's response bubble updated in real time so the user sees sub-thread progress
/// inline without waiting for completion.
///
/// No more <see cref="Task{String}"/> — the previous Task-returning shape forced the
/// FunctionInvokingChatClient to block on sub-thread completion, which deadlocks under
/// Orleans when the child's completion patch queues behind the parent hub scheduler.
/// </summary>
public static class DelegationTool
{
    /// <summary>
    /// Creates a unified delegation tool that can delegate to any available agent.
    /// Each delegation uses an isolated thread for the target agent.
    /// </summary>
    public static AITool CreateUnifiedDelegationTool(
        AgentConfiguration currentAgent,
        IReadOnlyList<AgentConfiguration> hierarchyAgents,
        Func<string, string, string?, CancellationToken, IAsyncEnumerable<string>> executeAsync,
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

        async IAsyncEnumerable<string> Delegate(
            [Description("The name of the agent to delegate to. Use the agentPath from the available agents.")] string agentName,
            [Description("The task or instructions for the delegated agent. Be specific about what you need.")] string task,
            [Description("Optional: the node path to use as context for this delegation (e.g., 'OrgA/my-doc'). When omitted, inherits the parent context. Set explicitly when delegating parallel work on different documents.")] string? context = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            logger?.LogInformation("Delegating to {AgentName}: {Task}, context={Context}",
                agentName, task, context ?? "(inherited)");

            await foreach (var chunk in executeAsync(agentName, task, context, cancellationToken)
                .WithCancellation(cancellationToken))
            {
                yield return chunk;
            }

            logger?.LogInformation("Delegation to {AgentName} stream completed", agentName);
        }

        var description = $"""
            Delegate to a specialized agent when the request matches their expertise.
            Each delegation runs in an isolated context - the agent won't see previous conversation history.
            The delegated agent's output streams back as it generates.

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
