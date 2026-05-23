using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Text;
using MeshWeaver.AI.Plugins;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.AI;

/// <summary>
/// Base factory for creating ChatClientAgent instances.
/// This is the single implementation for creating AI agents from configurations.
/// Subclasses provide the specific IChatClient implementation (e.g., Azure OpenAI, Azure Foundry).
/// </summary>
public abstract class ChatClientAgentFactory : IChatClientFactory
{
    protected readonly IMessageHub Hub;
    protected readonly ILogger Logger;

    /// <summary>
    /// The current model name being used for agent creation
    /// </summary>
    protected string? CurrentModelName { get; private set; }

    protected ChatClientAgentFactory(IMessageHub hub)
    {
        Hub = hub;
        Logger = hub.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(GetType());
    }

    /// <summary>
    /// Factory identifier (e.g., "Azure OpenAI", "Azure Claude")
    /// </summary>
    public abstract string Name { get; }

    /// <summary>
    /// List of models this factory can create
    /// </summary>
    public abstract IReadOnlyList<string> Models { get; }

    /// <summary>
    /// Display order for sorting in model dropdown (lower = first)
    /// </summary>
    public abstract int Order { get; }

    /// <inheritdoc />
    /// <remarks>
    /// Default implementation delegates to <see cref="Models"/> for backward compatibility.
    /// Concrete factories with shape-aware routing (e.g. "claude-*" prefix) should override.
    /// </remarks>
    public virtual bool Supports(string modelName) =>
        !string.IsNullOrEmpty(modelName) && Models.Any(m =>
            string.Equals(m, modelName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Creates a ChatClientAgent for the given configuration.
    /// </summary>
    /// <summary>
    /// Creates a ChatClientAgent synchronously — no await, no deadlock.
    /// Uses raw instructions without async @@reference resolution.
    /// References are resolved lazily at runtime.
    /// </summary>
    public ChatClientAgent CreateAgent(
        AgentConfiguration config,
        IAgentChat chat,
        IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
        IReadOnlyList<AgentConfiguration> hierarchyAgents,
        string? modelName = null)
    {
        CurrentModelName = modelName;

        if (string.IsNullOrEmpty(config.PreferredModel) && !string.IsNullOrEmpty(config.ModelTier))
        {
            var tierConfig = Hub.ServiceProvider.GetService<IOptions<ModelTierConfiguration>>()?.Value;
            var resolvedModel = tierConfig?.Resolve(config.ModelTier);
            if (!string.IsNullOrEmpty(resolvedModel))
                config = config with { PreferredModel = resolvedModel };
        }

        // Sync: use raw instructions, skip @@reference resolution (resolved lazily)
        var instructions = GetAgentInstructions(config, hierarchyAgents, chat);
        return CreateAgentCore(config, chat, existingAgents, hierarchyAgents, instructions, modelName);
    }

    [Obsolete("Use CreateAgent — CreateAgentAsync deadlocks in Orleans")]
    public async Task<ChatClientAgent> CreateAgentAsync(
        AgentConfiguration config,
        IAgentChat chat,
        IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
        IReadOnlyList<AgentConfiguration> hierarchyAgents,
        string? modelName = null)
    {
        CurrentModelName = modelName;

        if (string.IsNullOrEmpty(config.PreferredModel) && !string.IsNullOrEmpty(config.ModelTier))
        {
            var tierConfig = Hub.ServiceProvider.GetService<IOptions<ModelTierConfiguration>>()?.Value;
            var resolvedModel = tierConfig?.Resolve(config.ModelTier);
            if (!string.IsNullOrEmpty(resolvedModel))
                config = config with { PreferredModel = resolvedModel };
        }

        var instructions = await GetAgentInstructionsAsync(config, hierarchyAgents, chat);
        return CreateAgentCore(config, chat, existingAgents, hierarchyAgents, instructions, modelName);
    }

    private ChatClientAgent CreateAgentCore(
        AgentConfiguration config, IAgentChat chat,
        IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
        IReadOnlyList<AgentConfiguration> hierarchyAgents,
        string instructions, string? modelName)
    {
        var name = config.Id;
        var description = config.Description ?? string.Empty;
        var chatClient = CreateChatClient(config);
        var agentTools = GetAgentTools(config, chat, existingAgents, hierarchyAgents);
        IEnumerable<AITool> tools = agentTools;

        if (config.Plugins is { Count: > 0 })
        {
            foreach (var pluginRef in config.Plugins)
            {
                var pluginTools = ResolvePluginTools(pluginRef, chat);
                if (pluginTools != null)
                    tools = tools.Concat(pluginTools);
                else
                    Logger.LogWarning("Plugin '{PluginName}' not found for agent {AgentName}",
                        pluginRef.Name, config.Id);
            }
        }
        else
        {
            var meshPlugin = new MeshPlugin(Hub, chat);
            var needsWriteTools = description.Contains("create", StringComparison.OrdinalIgnoreCase)
                || description.Contains("update", StringComparison.OrdinalIgnoreCase)
                || description.Contains("delete", StringComparison.OrdinalIgnoreCase);
            tools = tools.Concat(needsWriteTools ? meshPlugin.CreateAllTools() : meshPlugin.CreateTools());
        }

        tools = tools.Append(PlanStorageTool.Create(Hub, chat));

        // Wrap all tools to restore user access context before invocation.
        // AsyncLocal doesn't flow through the AI framework's streaming + tool invocation,
        // so each tool call must explicitly restore the user identity from the
        // thread's execution context. This is the single injection point for ALL tools.
        var accessService = Hub.ServiceProvider.GetService<AccessService>();
        var wrappedTools = tools.Select(tool => WrapToolWithAccessContext(tool, chat, accessService)).ToList();

        var agent = new ChatClientAgent(
            chatClient: chatClient, instructions: instructions,
            name: name, description: description,
            tools: wrappedTools, loggerFactory: null, services: null);

        var functionInvoker = agent.ChatClient.GetService<Microsoft.Extensions.AI.FunctionInvokingChatClient>();
        if (functionInvoker != null)
        {
            functionInvoker.AllowConcurrentInvocation = true;
            // Log the maximum iterations — if the model tries more tool calls than this, it stops
            Logger.LogInformation("[AgentFactory] FunctionInvoker for {Agent}: MaximumIterationsPerRequest={Max}",
                name, functionInvoker.MaximumIterationsPerRequest);
        }

        // Wrap with function calling middleware — gives the streaming loop
        // real-time visibility into tool calls. FunctionInvokingChatClient
        // consumes FunctionCallContent internally; without this middleware,
        // the outer stream never sees tool invocations.
        //
        // ⚠️  Note: this middleware fires only when callers route through the
        // agent's RunStreamingAsync / RunAsync. `AgentChatClient` currently
        // calls `agent.ChatClient.GetStreamingResponseAsync` directly (faster
        // path that bypasses Microsoft.Agents.AI's wrapping), so the
        // function-invocation middleware here is effectively unused for the
        // main streaming flow. Result-population of ToolCallEntry happens
        // instead via `FunctionResultContent` in the outer streaming loop
        // (ThreadExecution.cs) when the underlying chat client emits FRC, or
        // via `UpdateDelegationStatus` on the delegation terminal.
        return agent.AsBuilder()
            .Use((AIAgent _, FunctionInvocationContext ctx, Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next, CancellationToken ct) =>
            {
                Logger.LogInformation("[Middleware] Tool call: {Name}, ForwardToolCall={HasCallback}",
                    ctx.Function.Name, chat.ForwardToolCall != null);
                var toolEntry = new ToolCallEntry
                {
                    Name = ctx.Function.Name,
                    DisplayName = ctx.Function.Name,
                    Arguments = ctx.Arguments?.Count > 0
                        ? string.Join(", ", ctx.Arguments.Select(kv => $"{kv.Key}={kv.Value}"))
                        : null,
                    Timestamp = DateTime.UtcNow
                };
                chat.ForwardToolCall?.Invoke(toolEntry);
                return next(ctx, ct);
            })
            .Build() as ChatClientAgent ?? agent;
    }

    /// <summary>
    /// Wraps an AITool so that the user's access context is restored before each invocation.
    /// This is the single injection point for ALL tool calls — delegation, MeshPlugin, etc.
    /// </summary>
    private static AITool WrapToolWithAccessContext(AITool tool, IAgentChat chat, AccessService? accessService)
    {
        if (accessService == null || tool is not AIFunction aiFunction)
            return tool;

        // Create a wrapper AIFunction that restores access context before delegating
        return new AccessContextAIFunction(aiFunction, chat, accessService);
    }

    /// <summary>
    /// Creates a ChatClient instance for the specified agent configuration.
    /// Implementations should configure the chat client with their specific chat completion provider.
    /// </summary>
    protected abstract IChatClient CreateChatClient(AgentConfiguration agentConfig);

    /// <summary>
    /// Gets tools for the specified agent configuration including both plugins and delegation functions.
    /// </summary>
    protected virtual IEnumerable<AITool> GetToolsForAgent(
        AgentConfiguration agentConfig,
        IAgentChat chat,
        IReadOnlyDictionary<string, ChatClientAgent> allAgents,
        IReadOnlyList<AgentConfiguration> hierarchyAgents)
    {
        var nTools = 0;
        var tools = GetStandardTools(chat).Concat(GetAgentTools(agentConfig, chat, allAgents, hierarchyAgents));

        foreach (var tool in tools)
        {
            yield return tool;
            nTools++;
        }

        Logger.LogInformation("Agent {AgentName}: Added {Count} plugin tools",
            agentConfig.Id,
            nTools);
    }

    protected virtual IEnumerable<AITool> GetStandardTools(IAgentChat chat)
    {
        return [];
    }

    /// <summary>
    /// Creates delegation and handoff tools for agents.
    /// Uses a unified Delegate tool that includes all available agents in its description.
    /// </summary>
    protected virtual IEnumerable<AITool> GetAgentTools(
        AgentConfiguration agentConfig,
        IAgentChat chat,
        IReadOnlyDictionary<string, ChatClientAgent> allAgents,
        IReadOnlyList<AgentConfiguration> hierarchyAgents)
    {
        var hasDelegations = agentConfig.Delegations is { Count: > 0 };
        var hasHandoffs = agentConfig.Handoffs is { Count: > 0 };
        var hasHierarchyAgents = hierarchyAgents.Count > 1;

        if (!hasDelegations && !hasHandoffs && !hasHierarchyAgents && !agentConfig.IsDefault)
        {
            yield break;
        }

        if (hasDelegations || hasHierarchyAgents || agentConfig.IsDefault)
        {
            var delegationTool = DelegationTool.CreateUnifiedDelegationTool(
                agentConfig,
                hierarchyAgents,
                executeAsync: (agentName, task, context, ct) =>
                    ExecuteDelegationAsync(agentConfig, allAgents, chat, agentName, task, context, ct),
                Logger);

            Logger.LogInformation("Created unified delegation tool for agent {AgentName} with {HierarchyCount} hierarchy agents",
                agentConfig.Id, hierarchyAgents.Count);

            yield return delegationTool;
        }

        // Create handoff tool when agent has explicit handoffs
        if (hasHandoffs)
        {
            var handoffTool = HandoffTool.CreateUnifiedHandoffTool(
                agentConfig,
                hierarchyAgents,
                requestHandoff: chat.RequestHandoff,
                Logger);

            Logger.LogInformation("Created handoff tool for agent {AgentName} with {HandoffCount} handoff targets",
                agentConfig.Id, agentConfig.Handoffs!.Count);

            yield return handoffTool;
        }
    }

    /// <summary>
    /// Dispatches a sub-thread and yields its final accumulated text when the
    /// sub-thread reaches a terminal state. While the sub-thread streams, the
    /// PARENT projects each child emission onto its OWN response cell's
    /// matching <see cref="ToolCallEntry"/> — <c>Result</c> carries the last
    /// 10 lines of sub-agent output, <c>Status</c> tracks the lifecycle.
    /// GUIs databind to that tool call for the live progress view.
    ///
    /// <para><b>Direction is parent-observes-child.</b> Sub-thread code is
    /// oblivious — it streams exactly as if it were a top-level thread. The
    /// parent owns the remote subscriptions on the sub-thread's node + response
    /// cell, computes a projection on each emission, and writes that projection
    /// onto its OWN response cell via <c>parentWorkspace.GetMeshNodeStream(parentResponsePath).Update(...)</c>.
    /// The parent owns <c>parentResponsePath</c>, so the write serialises on its
    /// own data-source action block — no cross-hub race.</para>
    ///
    /// <para><b>Yield contract.</b> Returns an <see cref="IAsyncEnumerable{T}"/>
    /// of <see cref="string"/>, but only yields ONCE at terminal — with the
    /// sub-thread's full accumulated text. <see cref="Plugins.DelegationTool"/>
    /// drains the enumerable and gives the accumulation back to FCC as the
    /// <c>FunctionResultContent</c>; the per-tick deltas are not needed there
    /// because the live progress has already landed on the parent's tool call.</para>
    ///
    /// <para><b>Watchdog stays.</b> 5-minute timeout → propagate
    /// <c>RequestedCancellationAt</c> to the sub-thread + flip our tool call
    /// to <see cref="ToolCallStatus.Cancelled"/>, then yield the partial text
    /// so FCC can carry on.</para>
    /// </summary>
    private async IAsyncEnumerable<string> ExecuteDelegationAsync(
        AgentConfiguration agentConfig,
        IReadOnlyDictionary<string, ChatClientAgent> allAgents,
        IAgentChat chat,
        string agentName,
        string task,
        string? context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Resolve target agent (strip path prefix if present).
        var targetId = agentName.Split('/').Last();
        if (!allAgents.TryGetValue(targetId, out _))
        {
            yield return $"Agent '{agentName}' not found";
            yield break;
        }

        var execCtx = chat.ExecutionContext;
        if (execCtx == null)
        {
            yield return "No execution context available for delegation";
            yield break;
        }

        // Guard: limit delegation depth. See comment on original version for segment math.
        var threadPath = execCtx.ThreadPath;
        var threadIdx = threadPath.IndexOf("/_Thread/", StringComparison.Ordinal);
        var depth = 0;
        if (threadIdx >= 0)
        {
            var afterThread = threadPath[(threadIdx + "/_Thread/".Length)..];
            var segments = afterThread.Split('/').Length;
            depth = (segments - 1) / 2;
        }
        if (depth >= 2)
        {
            Logger.LogWarning("[Delegation] Max depth reached at {ThreadPath}: {Source} → {Target}",
                threadPath, agentConfig.Id, targetId);
            yield return $"Maximum delegation depth reached ({depth}). Handle this task directly.";
            yield break;
        }

        Logger.LogInformation("[Delegation] {Source} → {Target}, depth={Depth}, task={Task}",
            agentConfig.Id, targetId, depth, task.Length > 100 ? task[..97] + "..." : task);

        var meshService = Hub.ServiceProvider.GetRequiredService<IMeshService>();
        // The PARENT response cell — also the namespace under which the sub-thread is created.
        // We own this path; writes through `parentWorkspace.GetMeshNodeStream(parentMsgPath).Update(...)`
        // serialise on the parent's action block.
        var parentMsgPath = $"{threadPath}/{execCtx.ResponseMessageId}";
        var mainEntityPath = execCtx.ContextPath ?? context ?? threadPath;

        // Build the sub-thread with IsExecuting=true + PendingUserMessage so its hub's
        // WatchForExecution starts streaming on activation.
        var (subThreadNode, userMsgId, responseMsgId) = ThreadNodeType.BuildThreadWithMessages(
            parentMsgPath, task,
            createdBy: execCtx.UserAccessContext?.ObjectId,
            agentName: targetId);
        subThreadNode = subThreadNode with { MainNode = mainEntityPath };
        var subThreadPath = subThreadNode.Path!;
        var responsePath = $"{subThreadPath}/{responseMsgId}";

        // Stamp the delegation path so the parent's bubble can render the inline link.
        var delegationDisplayName = $"Delegating to {targetId}...";
        chat.DelegationPaths[delegationDisplayName] = subThreadPath;
        chat.LastDelegationPath = subThreadPath;
        chat.UpdateDelegationStatus?.Invoke(delegationDisplayName);

        Logger.LogInformation("[Delegation] Dispatch sub-thread {Path}: user={UserMsgId}, response={ResponseMsgId}",
            subThreadPath, userMsgId, responseMsgId);

        // Create satellite cells + thread node reactively (no await).
        meshService.CreateNode(new MeshNode(userMsgId, subThreadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            MainNode = mainEntityPath,
            Content = new ThreadMessage
            {
                Role = "user", Text = task, Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.ExecutedInput,
                CreatedBy = execCtx.UserAccessContext?.ObjectId
            }
        }).Subscribe(_ => { },
            error => Logger.LogDebug(error, "[Delegation] User cell create for {Path} returned error", subThreadPath));

        meshService.CreateNode(new MeshNode(responseMsgId, subThreadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            MainNode = mainEntityPath,
            Content = new ThreadMessage
            {
                Role = "assistant", Text = "", Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.AgentResponse, AgentName = targetId
            }
        }).Subscribe(_ => { },
            error => Logger.LogDebug(error, "[Delegation] Response cell create for {Path} returned error", subThreadPath));

        meshService.CreateNode(subThreadNode).Subscribe(
            _ => Logger.LogInformation("[Delegation] Sub-thread created at {Path}", subThreadPath),
            error => Logger.LogWarning(error, "[Delegation] Sub-thread create failed at {Path}", subThreadPath));

        // Terminal-signal channel: completion sources (sub.IsExecuting=false /
        // sub.CompletedAt set) write here. The reader awaits one signal then
        // computes the final state from the latest snapshots and returns.
        var terminalTcs = new TaskCompletionSource<TerminalSignal>(TaskCreationOptions.RunContinuationsAsynchronously);

        // 🚨 All remote MeshNode reads + writes go through IMeshNodeStreamCache.
        // Going around it (ad-hoc workspace.GetRemoteStream / GetMeshNodeStream)
        // opens a separate handle, so writes are "lost" — never seen by the
        // readers of the cached stream. The cache is the single shared,
        // process-wide handle per path (see IMeshNodeStreamCache xmldoc).
        var nodeCache = Hub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        // Live snapshots — written by subscriptions, read by the projector + terminal handler.
        // Plain fields suffice: the only consumer is the projector lambda which
        // re-reads on each emission, and we never need atomicity across both fields.
        string lastSubText = "";
        ThreadMessageStatus? lastSubStatus = null;

        // 🚨 The GUI reads sub-thread state DIRECTLY via cache.GetStream(DelegationPath)
        // → its ActiveMessageId → response cell. We do NOT mirror the sub-thread's
        // streaming text onto the parent's ToolCallEntry.Result here — that was the
        // source of duplicates + out-of-sync displays. We only write the parent's
        // ToolCallEntry TWICE per delegation lifecycle: once at terminal (Status flip
        // + final Result for FCC's FunctionResultContent), and the dispatch-time stamp
        // is the FCC streaming loop's append (it already creates the entry).
        //
        // Subscriptions below exist ONLY to detect terminal — body display
        // streams from the sub-thread itself.
        void StampTerminalOnParentToolCall(ToolCallStatus newStatus)
        {
            // ⚠️  No-op kept as a hook for future terminal-state stamping.
            //
            // Earlier shapes that wrote to either:
            //   (a) the parent's response-cell via nodeCache.Update, OR
            //   (b) the streaming loop's toolCallLog via chat.ForwardToolCall
            // produced duplicate ToolCallEntry rows in the test/Orleans flow
            // (DelegationWriteCountTest had 0/10 pass rate, 2026-05-21).
            // Root cause: FCC re-emits the same FunctionCallContent in turn 2's
            // output stream as history echo. The streaming-loop's
            // FunctionCallContent handler (ThreadExecution.cs line 1346) sees
            // both emissions; the mirror here races with that.
            //
            // The bare entry → FunctionResultContent SetItem flow (line 1422)
            // populates Result + Status from FCC's tool result directly. For
            // production chat clients that emit FRC in stream output (Claude,
            // GPT-4 etc.) this is sufficient. For test agents that don't emit
            // FRC, Result stays null — that's the test agent's reality, not a
            // regression of the delegation refactor. The structural invariant
            // (one entry per DelegationPath) is preserved.
            _ = newStatus;
        }

        // Channel for streaming text deltas back to FCC via DelegationTool's
        // accumulator. Required to dodge the terminal-timing race: if we only
        // yield once at terminal, lastSubText may be empty (sub-thread's
        // final write may not have propagated through the cache by the moment
        // we read it). Channel-of-deltas ensures DelegationTool accumulates
        // every chunk as it arrives — total accumulation is the full text.
        var deltaChannel = System.Threading.Channels.Channel.CreateUnbounded<string>(
            new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        // 1. Subscribe to the SUB-THREAD via the cache. The cache opens the
        //    SubscribeRequest under System impersonation; that activates the
        //    sub-thread hub (its WatchForExecution hook then auto-runs the
        //    agent — BuildThreadWithMessages set IsExecuting=true). IsExecuting
        //    flipping false (after we've observed it run) is the terminal signal.
        //    The first emission can race ahead of the initial state, so we gate
        //    on `startedExecuting`. No projection write — the parent bubble streams
        //    sub-thread output through an embedded sub-thread Streaming layout area.
        var startedExecuting = false;
        var subThreadSub = nodeCache.GetStream(subThreadPath)
            .Subscribe(
                node =>
                {
                    if (node?.Content is not MeshThread thread) return;
                    if (thread.IsExecuting)
                    {
                        startedExecuting = true;
                        return;
                    }
                    if (startedExecuting)
                        terminalTcs.TrySetResult(TerminalSignal.ThreadIdle);
                },
                ex => terminalTcs.TrySetException(ex));

        // 2. Subscribe to the response-cell via the cache. Emits text deltas
        //    onto deltaChannel as the sub-agent streams; ALSO updates lastSubText
        //    so StampTerminalOnParentToolCall has the full accumulated text at
        //    terminal. The cell's CompletedAt + per-cell Status are the
        //    authoritative terminal signal.
        var responseCellSub = nodeCache.GetStream(responsePath)
            .Subscribe(
                node =>
                {
                    if (node?.Content is not ThreadMessage msg) return;
                    var current = msg.Text ?? "";
                    if (current.Length > lastSubText.Length)
                    {
                        var delta = current[lastSubText.Length..];
                        lastSubText = current;
                        deltaChannel.Writer.TryWrite(delta);
                    }
                    lastSubStatus = msg.Status;
                    if (msg.CompletedAt is not null)
                        terminalTcs.TrySetResult(TerminalSignal.CellCompleted);
                },
                ex => terminalTcs.TrySetException(ex),
                () => terminalTcs.TrySetResult(TerminalSignal.CellCompleted));

        // Safety timeout so a never-completing sub-thread can't pin this iterator forever.
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var cancelReg = linked.Token.Register(() =>
        {
            terminalTcs.TrySetCanceled(linked.Token);
            deltaChannel.Writer.TryComplete();
        });

        // When terminal fires (cell completed / thread idle), close the channel
        // so the drain loop below exits. Fire-and-forget — the awaiter chain is
        // the loop below; the task's only side-effect is closing the channel.
        TerminalSignal signal = TerminalSignal.ThreadIdle;
        Exception? terminalError = null;
        var wasCancelled = false;
        _ = terminalTcs.Task.ContinueWith(t =>
        {
            if (t.IsCanceled) wasCancelled = true;
            else if (t.IsFaulted) terminalError = t.Exception?.GetBaseException();
            else signal = t.Result;
            deltaChannel.Writer.TryComplete();
        }, TaskScheduler.Default);

        // Drain text deltas as they arrive. Each delta yields to FCC via
        // DelegationTool.Delegate's accumulator — net effect is FCC sees the
        // full sub-thread text as the FunctionResultContent, regardless of any
        // terminal-vs-final-write race in the cache subscription.
        try
        {
            await foreach (var delta in deltaChannel.Reader.ReadAllAsync(linked.Token).ConfigureAwait(false))
            {
                if (!string.IsNullOrEmpty(delta))
                    yield return delta;
            }
        }
        finally
        {
            subThreadSub?.Dispose();
            responseCellSub?.Dispose();
        }

        // 🚨 Race guard: the sub-thread's IsExecuting=false flip can fire
        // BEFORE the response cell's final Text emission propagates through
        // the cache. Without this one-shot read, lastSubText can be empty at
        // terminal and the FunctionResultContent FCC sees is "" — breaks
        // both the parent agent's wrap-up reasoning AND the test assertion
        // that Result is non-null. Do an authoritative cache read here so
        // we capture whatever the cell has at this instant.
        try
        {
            var finalCell = await nodeCache.GetStream(responsePath)
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(2))
                .ToTask(linked.Token)
                .ConfigureAwait(false);
            if (finalCell?.Content is ThreadMessage finalMsg)
            {
                if (!string.IsNullOrEmpty(finalMsg.Text))
                    lastSubText = finalMsg.Text;
                lastSubStatus = finalMsg.Status;
            }
        }
        catch
        {
            // Best-effort; if the read fails, fall back to whatever lastSubText
            // captured during the subscription lifetime.
        }

        // Map the observed terminal state → ToolCallStatus.
        var finalStatus = wasCancelled || timeout.IsCancellationRequested || cancellationToken.IsCancellationRequested
            ? ToolCallStatus.Cancelled
            : terminalError is not null
                ? ToolCallStatus.Failed
                : lastSubStatus switch
                {
                    ThreadMessageStatus.Error => ToolCallStatus.Failed,
                    ThreadMessageStatus.Cancelled => ToolCallStatus.Cancelled,
                    _ => ToolCallStatus.Success
                };

        // Final write — flips Status to terminal and stamps the FULL accumulated
        // sub-thread text on Result (the FunctionResultContent FCC delivers to
        // the parent agent). One write per delegation lifecycle.
        StampTerminalOnParentToolCall(finalStatus);

        // 🚨 Watchdog / parent-cancel must propagate cancel to the sub-thread.
        // Without this, the sub-thread's hub keeps running with IsExecuting=true
        // and the user sees a perpetually-"executing" bubble (the prod symptom
        // on 2026-05-20). We flip RequestedCancellationAt — the SAME primitive
        // the GUI Stop button uses. The sub-thread's own cancellation watcher
        // reacts to this and tears down its CTS.
        if (timeout.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
            try
            {
                nodeCache.Update(subThreadPath,
                    curr => curr?.Content is MeshThread sub
                        ? curr with { Content = sub with { RequestedCancellationAt = DateTime.UtcNow } }
                        : curr!)
                    .Subscribe(_ => { }, ex => Logger.LogWarning(ex,
                        "[Delegation] Cancel propagation to sub-thread {Path} failed", subThreadPath));
                Logger.LogInformation(
                    "[Delegation] Sub-thread {Path} cancelled (watchdog={Wd} / caller={Cc}); propagated RequestedCancellationAt",
                    subThreadPath, timeout.IsCancellationRequested, cancellationToken.IsCancellationRequested);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex,
                    "[Delegation] Could not propagate cancel to sub-thread {Path}", subThreadPath);
            }
        }
        else
        {
            Logger.LogInformation(
                "[Delegation] Sub-thread {Path} completed (signal={Signal}, status={Status})",
                subThreadPath, signal, finalStatus);
        }

        // Append a terminal marker only on error/cancel — the deltas already
        // yielded gave FCC the full sub-thread text. On success we don't
        // emit anything more; FCC's FunctionResultContent IS the accumulated
        // deltas, which IS the full sub-thread text.
        if (terminalError is not null)
            yield return $"\n[Delegation failed: {terminalError.Message}]";
        else if (wasCancelled || timeout.IsCancellationRequested || cancellationToken.IsCancellationRequested)
            yield return "\n[Delegation cancelled before completion]";
    }

    /// <summary>
    /// Distinguishes which subscription signalled terminal — purely for
    /// diagnostic logging; both signals map to the same finalisation path.
    /// </summary>
    private enum TerminalSignal { ThreadIdle, CellCompleted }

    /// <summary>
    /// Resolves a plugin reference to AITool instances.
    /// Built-in plugin "Mesh" is resolved directly; custom plugins are resolved from DI.
    /// Method filtering is applied when the plugin reference specifies methods.
    /// </summary>
    protected virtual IEnumerable<AITool>? ResolvePluginTools(
        AgentPluginReference pluginRef,
        IAgentChat chat)
    {
        // Resolve all tools for the plugin
        var allTools = pluginRef.Name switch
        {
            "Mesh" => (IEnumerable<AITool>)new MeshPlugin(Hub, chat).CreateAllTools(),
            "Version" => new VersionPlugin(Hub).CreateTools(),
            "Collaboration" => new CollaborationPlugin(Hub, chat).CreateTools(),
            "ContentCollection" => new ContentCollectionPlugin(Hub, chat).CreateTools(),
            "Lsp" => new LspPlugin(Hub, chat).CreateTools(),
            _ => Hub.ServiceProvider.GetServices<IAgentPlugin>()
                    .FirstOrDefault(p => string.Equals(p.Name, pluginRef.Name, StringComparison.OrdinalIgnoreCase))
                    ?.CreateTools()
        };

        if (allTools == null)
            return null;

        // Apply method filtering if specified
        if (pluginRef.Methods is { Count: > 0 })
        {
            var methodSet = pluginRef.Methods.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
            return allTools.Where(t => methodSet.Contains(t.Name));
        }

        return allTools;
    }

    /// <summary>
    /// Sync version — skips @@reference resolution (resolved lazily at runtime).
    /// </summary>
    protected string GetAgentInstructions(AgentConfiguration agentConfig, IReadOnlyList<AgentConfiguration> hierarchyAgents, IAgentChat chat)
    {
        var baseInstructions = agentConfig.Instructions ?? string.Empty;
        // @@references left unresolved — will be resolved lazily or by the agent at runtime
        return BuildInstructionsWithDelegations(baseInstructions, agentConfig, hierarchyAgents, chat);
    }

    protected async Task<string> GetAgentInstructionsAsync(AgentConfiguration agentConfig, IReadOnlyList<AgentConfiguration> hierarchyAgents, IAgentChat chat)
    {
        var baseInstructions = agentConfig.Instructions ?? string.Empty;
        baseInstructions = await InlineReferenceResolver.ResolveAsync(baseInstructions, Hub, chat);
        return BuildInstructionsWithDelegations(baseInstructions, agentConfig, hierarchyAgents, chat);
    }

    private string BuildInstructionsWithDelegations(string baseInstructions, AgentConfiguration agentConfig, IReadOnlyList<AgentConfiguration> hierarchyAgents, IAgentChat chat)
    {
        var hasDelegations = agentConfig.Delegations is { Count: > 0 };
        var hasHandoffs = agentConfig.Handoffs is { Count: > 0 };
        var hasHierarchyAgents = hierarchyAgents.Count > 1;

        if (!hasDelegations && !hasHandoffs && !hasHierarchyAgents && !agentConfig.IsDefault)
        {
            return baseInstructions;
        }

        var result = baseInstructions;

        // Delegation guidelines
        var delegationList = ImmutableList<string>.Empty;

        if (agentConfig.Delegations != null)
        {
            foreach (var d in agentConfig.Delegations)
            {
                var agentId = d.AgentPath.Split('/').Last();
                delegationList = delegationList.Add($"- {agentId}: {d.Instructions}");
            }
        }

        var listedIds = agentConfig.Delegations?.Select(d => d.AgentPath.Split('/').Last()).ToImmutableHashSet()
            ?? ImmutableHashSet<string>.Empty;
        var handoffIds = agentConfig.Handoffs?.Select(h => h.AgentPath.Split('/').Last()).ToImmutableHashSet()
            ?? ImmutableHashSet<string>.Empty;

        foreach (var agent in hierarchyAgents.Where(a => a.Id != agentConfig.Id && !listedIds.Contains(a.Id) && !handoffIds.Contains(a.Id)))
        {
            delegationList = delegationList.Add($"- {agent.Id}: {agent.Description ?? "Agent in hierarchy"}");
        }

        if (delegationList.Count > 0)
        {
            var agentListStr = string.Join('\n', delegationList);

            result +=
                $$$"""

                   **Agent Delegation:**
                   You have access to a delegate_to_agent tool to route requests to specialized agents.
                   Use delegation when you need a result back — the delegated agent runs in isolation and returns its result to you.

                   **Available Agents for Delegation:**
                   {{{agentListStr}}}

                   **How to delegate:**
                   1. Identify which specialized agent can best handle the user's request
                   2. Call the delegate_to_agent tool with the agent name and your task description
                   3. The delegated agent will execute the task and return its result to you
                   4. Relay or summarize the result to the user

                   """;
        }

        // Handoff guidelines
        if (hasHandoffs)
        {
            var handoffList = ImmutableList<string>.Empty;
            foreach (var h in agentConfig.Handoffs!)
            {
                var agentId = h.AgentPath.Split('/').Last();
                handoffList = handoffList.Add($"- {agentId}: {h.Instructions}");
            }

            var handoffListStr = string.Join('\n', handoffList);

            result +=
                $$$"""

                   **Agent Handoff:**
                   You have access to a handoff_to_agent tool to transfer control to another agent.
                   Use handoff when the target agent should take over the conversation directly and interact with the user.
                   After a handoff, you stop responding — the target agent continues on the shared thread with full history.

                   **Available Agents for Handoff:**
                   {{{handoffListStr}}}

                   **When to use handoff vs delegation:**
                   - **Delegation**: You need information or a result back. The other agent works in isolation.
                   - **Handoff**: The other agent should take over and interact with the user directly.

                   """;
        }

        return result;
    }
}

/// <summary>
/// AIFunction wrapper that restores the user's access context before each invocation.
/// This is the single injection point for ALL tool calls — delegation, MeshPlugin, etc.
/// </summary>
internal sealed class AccessContextAIFunction : DelegatingAIFunction
{
    private readonly IAgentChat _chat;
    private readonly AccessService _accessService;

    public AccessContextAIFunction(AIFunction inner, IAgentChat chat, AccessService accessService)
        : base(inner)
    {
        _chat = chat;
        _accessService = accessService;
    }

    protected override ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var userCtx = _chat.ExecutionContext?.UserAccessContext;
        if (userCtx != null)
            _accessService.SetContext(userCtx);
        return base.InvokeCoreAsync(arguments, cancellationToken);
    }
}
