using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using MeshWeaver.AI.Attributes;
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

        // The PARENT response cell — also the namespace under which the sub-thread is created.
        var parentMsgPath = $"{threadPath}/{execCtx.ResponseMessageId}";
        var mainEntityPath = execCtx.ContextPath ?? context ?? threadPath;

        // Build the full sub-thread node + ids ONCE. GenerateSpeakingId appends
        // a random suffix, so calling BuildThreadWithMessages twice produces
        // DIFFERENT paths. Single source of truth.
        var (preSubThreadNode, userMsgId, responseMsgId) =
            MeshWeaver.AI.ThreadNodeType.BuildThreadWithMessages(
                parentMsgPath, task,
                createdBy: execCtx.UserAccessContext?.ObjectId,
                agentName: targetId);
        var subThreadNode = preSubThreadNode with { MainNode = mainEntityPath };
        var subThreadPath = subThreadNode.Path!;
        var responsePath = $"{subThreadPath}/{responseMsgId}";
        var callId = Guid.NewGuid().ToString("N")[..8];

        Logger.LogDebug(
            "[Delegation:{CallId}] ENTER sub={SubPath} target={Target} parentResp={ParentResp}",
            callId, subThreadPath, targetId, parentMsgPath);

        var meshService = Hub.ServiceProvider.GetRequiredService<IMeshService>();
        var workspace = Hub.GetWorkspace();

        // 🚨 AWAIT the sub-thread create BEFORE emitting Dispatched or
        // subscribing to its streams. meshService.CreateNode emits OnNext when
        // the CreateNodeRequest's response lands — the node IS in storage by
        // then (see MeshService.cs:62). Emitting Dispatched too early lets
        // the heartbeat scanner (which reads cache.GetStream over
        // ActiveDelegationPaths) hit the cache before the node exists,
        // poisoning the shared entry.
        Logger.LogDebug("[Delegation:{CallId}] CREATE_BEGIN sub-thread", callId);
        string? createError = null;
        try
        {
            await meshService.CreateNode(subThreadNode)
                .Timeout(TimeSpan.FromSeconds(10))
                .FirstAsync()
                .ToTask(cancellationToken)
                .ConfigureAwait(false);
            Logger.LogDebug("[Delegation:{CallId}] CREATE_OK sub={Path}", callId, subThreadPath);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex,
                "[Delegation:{CallId}] CREATE_FAIL sub={Path}", callId, subThreadPath);
            createError = ex.Message;
        }
        if (createError is not null)
        {
            yield return $"\n[Delegation failed: {createError}]";
            yield break;
        }

        // Emit Dispatched onto chat.Delegations. AgentChatClient.EmitDelegationEvent
        // also updates ActiveDelegationPaths which the cancel watcher +
        // streaming-loop stamp pass read.
        if (chat is AgentChatClient agentChat)
        {
            Logger.LogDebug(
                "[Delegation:{CallId}] EMIT_DISPATCHED sub={Path}", callId, subThreadPath);
            agentChat.EmitDelegationEvent(
                new MeshWeaver.AI.Delegation.DelegationEvent(callId, subThreadPath,
                    MeshWeaver.AI.Delegation.DelegationLifecycle.Dispatched));
        }

        // Single channel — writer is the stream subscription; SINGLE READER
        // is the `await foreach` below on the FCC invocation thread. Delta
        // accumulation + terminal detection run single-threaded in the reader.
        var channel = System.Threading.Channels.Channel.CreateUnbounded<DelegationObservation>(
            new System.Threading.Channels.UnboundedChannelOptions
            { SingleReader = true, SingleWriter = false });
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
            ex => Logger.LogDebug(ex, "[Delegation] user cell create benign error at {Path}", subThreadPath));
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
            ex => Logger.LogDebug(ex, "[Delegation] response cell create benign error at {Path}", subThreadPath));

        // 🚨 BYPASS the cache for sub-thread + response-cell reads. The
        // process-wide MeshNodeStreamCache holds ONE shared subscription per
        // path and its ReplaySubject permanently captures OnError. Subscribing
        // before the sub-thread create completes would poison that shared
        // entry for every other consumer (heartbeat scanner, GUI, MCP). The
        // bypass opens a fresh per-call subscription via the workspace; the
        // handle stays an UPDATABLE MeshNodeStreamHandle (read + write) so
        // future hooks can also write through it. Wrapped in Defer + Catch +
        // Repeat(200ms) so the create-roundtrip window doesn't kill the stream.
        var subThreadHandle = workspace.GetMeshNodeStreamBypassCache(subThreadPath);
        var responseCellHandle = workspace.GetMeshNodeStreamBypassCache(responsePath);

        IObservable<MeshNode> ResilientStream(MeshNodeStreamHandle handle) =>
            System.Reactive.Linq.Observable.Defer(() => (IObservable<MeshNode>)handle)
                .Catch<MeshNode, Exception>(_ =>
                    System.Reactive.Linq.Observable.Empty<MeshNode>()
                        .Delay(TimeSpan.FromMilliseconds(200)))
                .Repeat();

        // ONE subscription. Lambda's ONLY job is channel.Writer.TryWrite —
        // schedulerless, lock-free. State-machine logic runs single-threaded
        // in the reader below.
        Logger.LogDebug("[Delegation:{CallId}] CACHE_SUB_INSTALL sub={Path} resp={Path2}",
            callId, subThreadPath, responsePath);
        using var cacheSub = System.Reactive.Linq.Observable.CombineLatest(
                ResilientStream(subThreadHandle),
                ResilientStream(responseCellHandle),
                (threadNode, cellNode) => (threadNode, cellNode))
            .Subscribe(
                tup => channel.Writer.TryWrite(new DelegationObservation(
                    Thread: tup.threadNode.Content as MeshThread,
                    Cell: tup.cellNode.Content as ThreadMessage,
                    Error: null)),
                ex =>
                {
                    Logger.LogWarning(ex,
                        "[Delegation:{CallId}] CACHE_SUB_ERROR sub={Path}", callId, subThreadPath);
                    channel.Writer.TryWrite(new DelegationObservation(
                        Thread: null, Cell: null, Error: ex.Message));
                });

        // Close the channel when the caller cancels the FCC turn.
        using var cancelReg = cancellationToken.Register(() =>
        {
            Logger.LogDebug("[Delegation:{CallId}] CANCEL_REQ_CALLER_TOKEN sub={Path}",
                callId, subThreadPath);
            channel.Writer.TryComplete();
        });

        // Single-reader drain. AccumulatedText, startedExecuting, finalStatus —
        // all mutated only here, no locks needed.
        var accumulatedText = "";
        var startedExecuting = false;
        var obsCount = 0;
        ThreadMessageStatus? finalStatus = null;
        string? terminalError = null;
        await foreach (var obs in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            obsCount++;
            if (obs.Error is not null)
            {
                Logger.LogDebug(
                    "[Delegation:{CallId}] OBS_ERROR #{Count} err={Err}",
                    callId, obsCount, obs.Error);
                terminalError = obs.Error;
                break;
            }
            Logger.LogDebug(
                "[Delegation:{CallId}] OBS #{Count} threadStatus={ThreadStatus} cellStatus={CellStatus} cellTextLen={Len} cellCompleted={Done}",
                callId, obsCount,
                obs.Thread?.Status, obs.Cell?.Status, obs.Cell?.Text?.Length ?? 0,
                obs.Cell?.CompletedAt is not null);

            // Text deltas — yield only the new portion.
            if (obs.Cell?.Text is { Length: > 0 } text && text.Length > accumulatedText.Length)
            {
                var delta = text[accumulatedText.Length..];
                accumulatedText = text;
                yield return delta;
            }

            // Terminal detection. Cell completion is authoritative; thread-idle
            // is the fallback for cases where the cell never gets a CompletedAt
            // (e.g. cancelled before first token).
            if (obs.Thread is { IsExecuting: true }) startedExecuting = true;
            var cellDone = obs.Cell?.CompletedAt is not null;
            var threadIdle = obs.Thread is { IsExecuting: false };
            if (cellDone || (startedExecuting && threadIdle))
            {
                finalStatus = obs.Cell?.Status;
                Logger.LogDebug(
                    "[Delegation:{CallId}] TERMINAL final={Final} cellDone={CellDone} threadIdle={Idle}",
                    callId, finalStatus, cellDone, threadIdle);
                break;
            }
        }
        Logger.LogDebug(
            "[Delegation:{CallId}] DRAIN_EXIT obs={Count} terminalError={Err} finalStatus={Status}",
            callId, obsCount, terminalError, finalStatus);

        // Emit Terminal so the cancel watcher + stamper drop this delegation.
        if (chat is AgentChatClient agentChat2)
            agentChat2.EmitDelegationEvent(
                new MeshWeaver.AI.Delegation.DelegationEvent(callId, subThreadPath,
                    MeshWeaver.AI.Delegation.DelegationLifecycle.Terminal));

        Logger.LogInformation(
            "[Delegation] Sub-thread {Path} settled (status={Status}, error={Error})",
            subThreadPath, finalStatus, terminalError ?? "(none)");

        if (terminalError is not null)
            yield return $"\n[Delegation failed: {terminalError}]";
        else if (cancellationToken.IsCancellationRequested
                 || finalStatus == ThreadMessageStatus.Cancelled)
            yield return "\n[Delegation cancelled before completion]";
    }

    private readonly record struct DelegationObservation(
        MeshThread? Thread,
        ThreadMessage? Cell,
        string? Error);

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
/// AIFunction wrapper that restores the user's access context before each invocation
/// AND enforces a per-tool execution timeout (via <see cref="ToolTimeoutAttribute"/>;
/// default 30 s). This is the single injection point for ALL tool calls — delegation,
/// MeshPlugin, etc.
///
/// <para>The timeout is read once at wrap time from the inner function's underlying
/// method. On expiry the linked CTS cancels the tool invocation and the agent receives
/// the synthetic "timed out" message as the tool result — never a hung promise.
/// <c>delegate_to_agent</c> is exempt (lifecycle-managed by the thread-hub heartbeat,
/// not a tool in the timeout-attribute sense).</para>
/// </summary>
internal sealed class AccessContextAIFunction : DelegatingAIFunction
{
    /// <summary>
    /// Default timeout when no <see cref="ToolTimeoutAttribute"/> is present on the
    /// underlying tool method. 30 s — long enough for any reasonable tool, short
    /// enough that a hung tool surfaces fast in the chat UI.
    /// </summary>
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Tools that opt out of the timeout because their lifecycle is managed by the
    /// thread hub itself (currently just <c>delegate_to_agent</c>). They have their
    /// own heartbeat-based hang detection on <c>MeshThread.LastActivityAt</c>.
    /// </summary>
    private static readonly HashSet<string> TimeoutExemptTools = new(StringComparer.Ordinal)
    {
        "delegate_to_agent",
    };

    private readonly IAgentChat _chat;
    private readonly AccessService _accessService;
    private readonly TimeSpan? _timeout;

    public AccessContextAIFunction(AIFunction inner, IAgentChat chat, AccessService accessService)
        : base(inner)
    {
        _chat = chat;
        _accessService = accessService;
        _timeout = TimeoutExemptTools.Contains(inner.Name)
            ? null
            : (inner.UnderlyingMethod?.GetCustomAttribute<ToolTimeoutAttribute>()?.Timeout
                ?? DefaultTimeout);
    }

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var userCtx = _chat.ExecutionContext?.UserAccessContext;
        if (userCtx != null)
            _accessService.SetContext(userCtx);

        if (_timeout is null)
            return await base.InvokeCoreAsync(arguments, cancellationToken);

        // Bound the wait via Task.WaitAsync — covers both well-behaved tools
        // (which observe cts.Token and unwind via OCE) AND ill-behaved tools
        // (which ignore the token and would otherwise pin the agent loop until
        // their intrinsic delay finishes). On timeout, the inner Task becomes
        // orphaned (still runs to completion in the background) but the agent
        // never waits — it gets a deterministic synthetic FunctionResultContent.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var invocation = base.InvokeCoreAsync(arguments, cts.Token).AsTask();
        try
        {
            return await invocation.WaitAsync(_timeout.Value, cancellationToken);
        }
        catch (TimeoutException)
        {
            // Our timer fired. Signal cooperative cancellation so well-behaved
            // tools wind down even though we've stopped waiting; ill-behaved
            // tools continue but the wrapper is no longer blocked on them.
            cts.Cancel();
            return $"Tool '{Name}' timed out after {_timeout.Value.TotalSeconds:F0}s. " +
                   $"Add [ToolTimeout(N)] to allow longer.";
        }
    }
}
