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
/// Snapshot of a currently-running (or recently-completed) sub-thread. Returned
/// by <see cref="DelegationTool"/>'s <c>list_sub_threads</c> tool so the parent
/// agent can see what delegations it has in flight and decide whether to send a
/// follow-up message via <c>send_to_sub_thread</c> or to wait.
/// </summary>
/// <param name="ThreadPath">Full mesh path of the sub-thread node.</param>
/// <param name="AgentName">The agent assigned to handle the delegation.</param>
/// <param name="Status">Idle / Executing / Completing — same enum as <c>Thread.Status</c>.</param>
/// <param name="PreviewText">First ~200 chars of the sub-thread's current response cell text (may be empty before the agent emits any text).</param>
/// <param name="LastActivity">Heartbeat / last token timestamp; lets the agent gauge progress.</param>
public record SubThreadInfo(
    string ThreadPath,
    string AgentName,
    string Status,
    string? PreviewText,
    DateTimeOffset? LastActivity);

/// <summary>
/// Creates delegation tools for agents that support isolated context per delegation.
///
/// Three tools per agent are emitted by <see cref="CreateDelegationTools"/>:
/// <list type="bullet">
///   <item><c>delegate_to_agent</c> — spawns a sub-thread, returns the sub-agent's
///         dedicated summary on completion. Tool-call result.</item>
///   <item><c>list_sub_threads</c> — read-only snapshot of active sub-threads
///         the parent has spawned (path, agent, status, preview, last activity).</item>
///   <item><c>send_to_sub_thread</c> — fire-and-forget mid-stream follow-up
///         message to a still-running sub-thread (writes to its
///         <c>PendingUserMessages</c> via <c>stream.Update</c>).</item>
/// </list>
///
/// <para><b>Completion semantics.</b> The current <c>delegate_to_agent</c> still
/// drains the sub-thread's <see cref="IAsyncEnumerable{String}"/> chunks on a
/// <c>Task.Run</c> with a TCS. The next reactive pass will switch this to
/// subscribe to the sub-thread's MeshNode stream and resolve the TCS when
/// <c>Status</c> flips back to <c>Idle</c> after execution, returning the
/// dedicated summary the sub-agent writes to the thread before exiting.</para>
///
/// <para>Sub-thread progress is additionally visible inline via the side-channel
/// <c>ToolCallEntry.DelegationPath</c> → the sub-thread's <c>Streaming</c> layout area.</para>
/// </summary>
public static class DelegationTool
{
    /// <summary>
    /// Creates the suite of delegation tools: <c>delegate_to_agent</c>, plus
    /// <c>list_sub_threads</c> and <c>send_to_sub_thread</c> when the parent
    /// passes the needed accessor delegates.
    /// </summary>
    public static IEnumerable<AITool> CreateDelegationTools(
        AgentConfiguration currentAgent,
        IReadOnlyList<AgentConfiguration> hierarchyAgents,
        Func<string, string, string?, CancellationToken, IAsyncEnumerable<string>> executeAsync,
        Func<IReadOnlyList<SubThreadInfo>>? listSubThreads = null,
        Action<string, string>? sendToSubThread = null,
        ILogger? logger = null)
    {
        yield return CreateUnifiedDelegationTool(
            currentAgent, hierarchyAgents, executeAsync, listSubThreads != null, sendToSubThread != null, logger);

        if (listSubThreads is not null)
            yield return CreateListSubThreadsTool(listSubThreads, logger);

        if (sendToSubThread is not null)
            yield return CreateSendToSubThreadTool(sendToSubThread, logger);
    }

    /// <summary>
    /// Creates a unified delegation tool that can delegate to any available agent.
    /// Each delegation uses an isolated thread for the target agent.
    /// </summary>
    public static AITool CreateUnifiedDelegationTool(
        AgentConfiguration currentAgent,
        IReadOnlyList<AgentConfiguration> hierarchyAgents,
        Func<string, string, string?, CancellationToken, IAsyncEnumerable<string>> executeAsync,
        ILogger? logger = null)
        => CreateUnifiedDelegationTool(currentAgent, hierarchyAgents, executeAsync,
            hasListTool: false, hasSendTool: false, logger);

    private static AITool CreateUnifiedDelegationTool(
        AgentConfiguration currentAgent,
        IReadOnlyList<AgentConfiguration> hierarchyAgents,
        Func<string, string, string?, CancellationToken, IAsyncEnumerable<string>> executeAsync,
        bool hasListTool,
        bool hasSendTool,
        ILogger? logger)
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
            //
            // 🚧 Next refactor: switch to GetRemoteStream<MeshNode>(subThreadPath)
            //    .Where(Status == Idle).FirstAsync() with summary read from thread.
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

        var coordinationGuidance = (hasListTool, hasSendTool) switch
        {
            (true, true) => """

                You can launch MULTIPLE delegations in parallel and coordinate them as they run:
                  - Call `list_sub_threads` at any time during your turn to see what delegations
                    you have in flight, their status (Executing / Completing / Idle), a preview
                    of the partial response, and last-activity timestamp. Use this to decide
                    whether to wait, to nudge a stuck agent, or to abandon a path.
                  - Call `send_to_sub_thread(threadPath, message)` to inject a mid-stream
                    follow-up into a running sub-thread (e.g. a clarification, a correction,
                    or "summarize and stop now"). The sub-agent picks the message up via its
                    pending-messages queue before producing its summary.
                Sub-agents are instructed to produce a DEDICATED SUMMARY before returning; that
                summary is what comes back as the tool result of `delegate_to_agent`.
                """,
            (true, false) => """

                You can launch MULTIPLE delegations in parallel and inspect them as they run:
                  - Call `list_sub_threads` at any time to see active delegations, their status,
                    a preview of the partial response, and last-activity timestamp.
                Sub-agents produce a dedicated summary before returning; that summary is the
                tool result of `delegate_to_agent`.
                """,
            (false, true) => """

                You can send follow-up messages to running sub-threads via
                `send_to_sub_thread(threadPath, message)` — useful for clarifying or steering
                an in-progress delegation. Sub-agents pick the message up via their pending-
                messages queue. Sub-agents produce a dedicated summary before returning.
                """,
            _ => ""
        };

        var description = $"""
            Delegate to a specialized agent when the request matches their expertise.
            Each delegation runs in an isolated context - the agent won't see previous conversation history.
            The delegated agent's output streams inline in the parent conversation via a nested streaming
            view, and the dedicated summary the sub-agent produces at the end is returned as the tool result.

            When delegating parallel work on different documents, set the 'context' parameter to the
            specific node path for each delegation. This ensures each agent sees the correct document.
            When omitted, the parent's context is inherited.
            {coordinationGuidance}
            Available agents:
            {agentsJson}

            Choose the most appropriate agent based on the user's request.
            """;

        return AIFunctionFactory.Create(
            Delegate,
            name: "delegate_to_agent",
            description: description);
    }

    /// <summary>
    /// Tool that returns a JSON snapshot of currently-active sub-threads (path,
    /// agent, status, preview text, last activity). Read-only; no side effects.
    /// </summary>
    private static AITool CreateListSubThreadsTool(
        Func<IReadOnlyList<SubThreadInfo>> listSubThreads,
        ILogger? logger)
    {
        string ListSubThreads()
        {
            var info = listSubThreads();
            logger?.LogInformation("list_sub_threads → {Count} active delegations", info.Count);
            return JsonSerializer.Serialize(info, new JsonSerializerOptions
            {
                WriteIndented = false,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        }

        return AIFunctionFactory.Create(
            ListSubThreads,
            name: "list_sub_threads",
            description: """
                List every sub-thread you have currently spawned via delegate_to_agent.
                Returns an array of { threadPath, agentName, status, previewText, lastActivity }
                so you can see which delegations are still running, which are completing,
                and roughly what each agent has produced so far. Use this when you want to
                decide whether to wait for a delegation, nudge it with send_to_sub_thread,
                or pivot to a different approach.
                """);
    }

    /// <summary>
    /// Tool that pushes a follow-up user message into an in-flight sub-thread's
    /// pending-messages queue. The sub-agent ingests it before producing its
    /// next response chunk; the parent does not wait for ack here (the parent
    /// already drains the sub-thread's stream via the original delegate_to_agent
    /// tool call).
    /// </summary>
    private static AITool CreateSendToSubThreadTool(
        Action<string, string> sendToSubThread,
        ILogger? logger)
    {
        string SendToSubThread(
            [Description("Full thread path of the sub-thread (from list_sub_threads).")] string threadPath,
            [Description("Message to send to the sub-agent — clarification, correction, or steering instruction.")] string message)
        {
            logger?.LogInformation("send_to_sub_thread {ThreadPath} (msgLen={Len})", threadPath, message.Length);
            sendToSubThread(threadPath, message);
            return "Queued";
        }

        return AIFunctionFactory.Create(
            SendToSubThread,
            name: "send_to_sub_thread",
            description: """
                Push a follow-up message into a running sub-thread's pending queue.
                The sub-agent picks it up via its inbox-drain mechanism before producing
                its next chunk. Use this to clarify ("focus on chapter 3 only"), correct
                ("ignore the previous mistake and restart from X"), or steer ("summarize
                and stop now"). Returns "Queued" immediately — the sub-agent's response to
                your follow-up appears in the same sub-thread stream you already see via
                the nested streaming view.
                """);
    }
}
