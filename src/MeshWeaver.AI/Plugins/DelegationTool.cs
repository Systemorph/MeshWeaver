using System.Collections.Immutable;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI.Plugins;

/// <summary>
/// Result record preserved for tests + <see cref="ThreadExecution.ExtractToolResult"/>.
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
/// The tool drains the sub-thread's stream on a ThreadPool <c>Task.Run</c> with
/// <c>ConfigureAwait(false)</c>, and exposes completion through a TCS-backed
/// <see cref="Task{String}"/>. The caller (FunctionInvokingChatClient) still awaits
/// the task, but its await is resolved from a non-hub thread — this is the standard
/// MeshWeaver "Post + RegisterCallback" shape and it does not capture the Orleans
/// grain scheduler. The prior <see cref="IAsyncEnumerable{String}"/> shape wedged
/// the grain whenever a sub-thread continuation had to post back through the same
/// scheduler the parent was awaiting on.
///
/// Sub-thread progress is additionally visible inline via the side-channel
/// <c>ToolCallEntry.DelegationPath</c> → the sub-thread's <c>Streaming</c> layout area.
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

        Task<string> Delegate(
            [Description("The name of the agent to delegate to. Use the agentPath from the available agents.")] string agentName,
            [Description("The task or instructions for the delegated agent. Be specific about what you need.")] string task,
            [Description("Optional: the node path to use as context for this delegation (e.g., 'OrgA/my-doc'). When omitted, inherits the parent context. Set explicitly when delegating parallel work on different documents.")] string? context = null,
            CancellationToken cancellationToken = default)
        {
            logger?.LogInformation("Delegating to {AgentName}: {Task}, context={Context}",
                agentName, task, context ?? "(inherited)");

            // Drain the sub-thread's enumerable on ThreadPool with ConfigureAwait(false).
            // This is the MeshWeaver "Post + RegisterCallback" shape: the caller awaits a
            // TCS-backed Task resolved from a non-hub thread, so the grain scheduler is
            // never captured on sub-thread continuations. The previous `async IAsyncEnumerable`
            // shape let FunctionInvokingChatClient capture the grain scheduler on every
            // iteration, wedging it whenever a sub-thread continuation needed to post back
            // through the same scheduler — the Orleans deadlock.
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            _ = Task.Run(async () =>
            {
                var sb = new StringBuilder();
                try
                {
                    await foreach (var chunk in executeAsync(agentName, task, context, cancellationToken)
                        .WithCancellation(cancellationToken).ConfigureAwait(false))
                    {
                        sb.Append(chunk);
                    }
                    tcs.TrySetResult(sb.ToString());
                    logger?.LogInformation("Delegation to {AgentName} stream completed", agentName);
                }
                catch (OperationCanceledException)
                {
                    tcs.TrySetCanceled(cancellationToken);
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Delegation to {AgentName} failed", agentName);
                    tcs.TrySetException(ex);
                }
            });

            return tcs.Task;
        }

        var description = $"""
            Delegate to a specialized agent when the request matches their expertise.
            Each delegation runs in an isolated context - the agent won't see previous conversation history.
            The delegated agent's output streams inline in the parent conversation via a nested streaming
            view, and the aggregated text is also returned as the tool result.

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
