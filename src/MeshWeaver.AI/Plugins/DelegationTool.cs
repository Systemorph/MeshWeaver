using System.Collections.Immutable;
using System.ComponentModel;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using MeshThread = MeshWeaver.AI.Thread;

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
/// <para><b>Completion semantics.</b> <c>delegate_to_agent</c> wraps the sub-thread's
/// streaming pipeline in <c>Observable.Create</c> + <c>Subscribe</c>: the
/// only <c>await foreach</c> in the entire delegation path runs on the subscriber's
/// continuation (the parent agent loop's task scheduler), no <c>Task.Run</c>. Sub-thread
/// completion (Status → Idle, response cell Status = Completed) terminates the inner
/// async-enumerable; <c>OnCompleted</c> resolves the TCS with the aggregated summary
/// text the sub-agent wrote to its response cell before exiting.</para>
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
    ///
    /// <para>When <paramref name="delegationEvents"/> + <paramref name="workspace"/>
    /// are supplied, <c>delegate_to_agent</c> resolves its <c>Task&lt;string&gt;</c>
    /// reactively: the next <c>Dispatched</c> event after invocation gives us the
    /// sub-thread path, then a subscription to <c>workspace.GetRemoteStream&lt;MeshNode&gt;(subThreadPath)</c>
    /// waits for <c>Thread.Status == Idle</c> (terminal), reads the sub-agent's
    /// final assistant message text, and resolves the TCS with that summary —
    /// no <c>Task.Run</c>, no chunk-aggregation race.</para>
    /// </summary>
    public static IEnumerable<AITool> CreateDelegationTools(
        AgentConfiguration currentAgent,
        IReadOnlyList<AgentConfiguration> hierarchyAgents,
        Func<string, string, string?, CancellationToken, IObservable<string>> executeAsync,
        Func<IReadOnlyList<SubThreadInfo>>? listSubThreads = null,
        Action<string, string>? sendToSubThread = null,
        IObservable<MeshWeaver.AI.Delegation.DelegationEvent>? delegationEvents = null,
        IWorkspace? workspace = null,
        ILogger? logger = null)
    {
        yield return CreateUnifiedDelegationTool(
            currentAgent, hierarchyAgents, executeAsync,
            listSubThreads != null, sendToSubThread != null,
            delegationEvents, workspace, logger);

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
        Func<string, string, string?, CancellationToken, IObservable<string>> executeAsync,
        ILogger? logger = null)
        => CreateUnifiedDelegationTool(currentAgent, hierarchyAgents, executeAsync,
            hasListTool: false, hasSendTool: false,
            delegationEvents: null, workspace: null, logger);

    private static AITool CreateUnifiedDelegationTool(
        AgentConfiguration currentAgent,
        IReadOnlyList<AgentConfiguration> hierarchyAgents,
        Func<string, string, string?, CancellationToken, IObservable<string>> executeAsync,
        bool hasListTool,
        bool hasSendTool,
        IObservable<MeshWeaver.AI.Delegation.DelegationEvent>? delegationEvents,
        IWorkspace? workspace,
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

            // Reactive completion: pure observable composition. The only `await
            // foreach` in the entire delegation path runs inside Observable.Create
            // on the subscriber's continuation (TaskScheduler.Current at Subscribe
            // time = the parent agent loop's task scheduler — Orleans grain in
            // prod, default in monolith tests). No Task.Run, no callback-bag.
            //
            // PRIMARY completion signal (when delegationEvents + workspace are
            // wired by ChatClientAgentFactory): subscribe to delegationEvents for
            // the next Dispatched (captures sub-thread path), then subscribe to
            // workspace.GetRemoteStream<MeshNode>(subThreadPath), wait for
            // Thread.Status flipping back to Idle AFTER ExecutionStartedAt was set
            // (= post-execution Idle), and read the last assistant ThreadMessage's
            // Text as the sub-agent's dedicated summary. Resolve TCS with that.
            //
            // FALLBACK (legacy callers without delegationEvents/workspace, or
            // when the reactive path doesn't fire in time): the IAsyncEnumerable's
            // OnCompleted resolves the TCS with the aggregated chunk text. The
            // IAsyncEnumerable also drives the sub-thread setup as a side effect,
            // so it MUST be subscribed even when the reactive completion path
            // would handle the result — otherwise the sub-thread never starts.
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sb = new StringBuilder();

            if (delegationEvents is not null && workspace is not null)
            {
                delegationEvents
                    .Where(e => e.Phase == MeshWeaver.AI.Delegation.DelegationLifecycle.Dispatched)
                    .Take(1)
                    .Subscribe(dispatched =>
                    {
                        var subThreadPath = dispatched.SubThreadPath;
                        logger?.LogInformation(
                            "Delegation Dispatched: sub-thread={SubPath}, callId={CallId} — subscribing for Idle",
                            subThreadPath, dispatched.CallId);

                        // Subscribe to the sub-thread node's stream and capture
                        // the Running → Idle transition. We use Scan to remember
                        // whether we've seen a non-Idle (Executing / Completing)
                        // status; the first Idle emission after that is the
                        // genuine post-execution terminal, NOT the initial-Idle
                        // emission the synced query replays on subscribe.
                        workspace.GetMeshNodeStream(subThreadPath)
                            .Select(node => node?.Content as MeshThread)
                            .Where(t => t is not null)
                            .Scan(
                                (sawRunning: false, terminal: (MeshThread?)null),
                                (state, t) =>
                                {
                                    if (t!.Status is ThreadExecutionStatus.Executing
                                                  or ThreadExecutionStatus.StartingExecution
                                                  or ThreadExecutionStatus.Completing)
                                        return (true, null);
                                    if (state.sawRunning && t.Status == ThreadExecutionStatus.Idle)
                                        return (state.sawRunning, t);
                                    return state;
                                })
                            .Where(s => s.terminal is not null)
                            .Take(1)
                            .Timeout(TimeSpan.FromMinutes(10))
                            .Subscribe(
                                s =>
                                {
                                    // Thread.Summary IS the agent's tool-call result —
                                    // written by ExecuteMessageAsync in the same
                                    // stream.Update cycle as Status → Idle, so this
                                    // emission carries the summary atomically.
                                    var summary = s.terminal!.Summary ?? "";
                                    logger?.LogInformation(
                                        "Sub-thread {SubPath} Running→Idle: summary len={Len}",
                                        subThreadPath, summary.Length);
                                    tcs.TrySetResult(summary);
                                },
                                ex => logger?.LogWarning(ex,
                                    "Sub-thread {SubPath} Running→Idle wait failed; falling back to chunk aggregate",
                                    subThreadPath));
                    });
            }

            // Pure Subscribe — executeAsync now returns IObservable<string> directly
            // (ExecuteDelegationAsync builds the sub-thread node via meshService.CreateNode
            // and Emits Dispatched; the parent's TCS is resolved by the Idle subscription
            // above which reads Thread.Summary). No await foreach, no Task.Run, no
            // Observable.Create wrapper.
            executeAsync(agentName, task, context, cancellationToken)
            .Subscribe(
                chunk => sb.Append(chunk),
                ex =>
                {
                    logger?.LogError(ex, "Delegation to {AgentName} failed", agentName);
                    if (ex is OperationCanceledException) tcs.TrySetCanceled(cancellationToken);
                    else tcs.TrySetException(ex);
                },
                () =>
                {
                    // TrySetResult is idempotent — if the reactive Idle path
                    // already resolved with the dedicated summary, this is a no-op.
                    if (tcs.TrySetResult(sb.ToString()))
                    {
                        logger?.LogInformation(
                            "Delegation to {AgentName} completed via chunk-aggregate fallback (len={Len})",
                            agentName, sb.Length);
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
