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
    /// <summary>The owning message hub — source of the service provider, workspace, and mesh services this factory uses to build agents.</summary>
    protected readonly IMessageHub Hub;
    /// <summary>Logger for this factory instance, categorised to the concrete factory type.</summary>
    protected readonly ILogger Logger;

    /// <summary>
    /// The current model name being used for agent creation
    /// </summary>
    protected string? CurrentModelName { get; private set; }

    /// <summary>
    /// Initialises the factory with its owning hub and resolves a type-categorised logger from it.
    /// </summary>
    /// <param name="hub">The message hub that owns this factory; supplies the service provider, workspace, and mesh services.</param>
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
    /// Resolves the agent's <see cref="AgentConfiguration.ModelTier"/> ("heavy" / "standard" /
    /// "light" / "utility") to a concrete model via the <c>ModelTier:*</c> config section.
    /// Returns null when the agent declares no tier, the tier isn't configured, or this
    /// factory doesn't serve the resolved model (so the caller falls back to its provider
    /// default instead of creating a client for a model another factory owns).
    /// Precedence in concrete factories: composer selection (<see cref="CurrentModelName"/>)
    /// → agent tier → provider default.
    /// </summary>
    protected string? ResolveTierModel(AgentConfiguration agentConfig)
    {
        if (string.IsNullOrEmpty(agentConfig.ModelTier))
            return null;

        var configuration = Hub.ServiceProvider.GetService<Microsoft.Extensions.Configuration.IConfiguration>();
        if (configuration == null)
            return null;

        var tiers = new ModelTierConfiguration
        {
            Heavy = configuration["ModelTier:Heavy"],
            Standard = configuration["ModelTier:Standard"],
            Light = configuration["ModelTier:Light"],
            Utility = configuration["ModelTier:Utility"]
        };

        var resolved = tiers.Resolve(agentConfig.ModelTier);
        if (string.IsNullOrEmpty(resolved) || !Supports(resolved))
            return null;

        Logger.LogDebug("[AgentFactory] Agent {Agent} tier '{Tier}' resolved to model {Model}",
            agentConfig.Id, agentConfig.ModelTier, resolved);
        return resolved;
    }

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

        // The composer selection (ThreadComposer.ModelName → modelName / CurrentModelName) always
        // wins. When nothing was selected (headless flows: email routing, notification triage,
        // delegated sub-threads), concrete factories fall back to the agent's ModelTier via
        // ResolveTierModel, then to their provider default.

        // Sync: use raw instructions, skip @@reference resolution (resolved lazily)
        var instructions = GetAgentInstructions(config, hierarchyAgents, chat);
        return CreateAgentCore(config, chat, existingAgents, hierarchyAgents, instructions, modelName);
    }

    /// <summary>
    /// Obsolete async agent creation — resolves @@references in instructions before building the
    /// agent. Deadlocks under Orleans (the await captures the grain scheduler); use <c>CreateAgent</c>
    /// instead, which builds synchronously and resolves references lazily at runtime.
    /// </summary>
    /// <param name="config">The agent configuration to build the chat client agent from.</param>
    /// <param name="chat">The chat session the agent participates in (supplies execution context and tool callbacks).</param>
    /// <param name="existingAgents">Already-built agents in the hierarchy, keyed by id, available for delegation wiring.</param>
    /// <param name="hierarchyAgents">All agent configurations in the hierarchy, used to build delegation/handoff tools and instructions.</param>
    /// <param name="modelName">The composer-selected model to use, or <c>null</c> to fall back to the agent's tier then the provider default.</param>
    /// <returns>The constructed <c>ChatClientAgent</c>.</returns>
    [Obsolete("Use CreateAgent — CreateAgentAsync deadlocks in Orleans")]
    public async Task<ChatClientAgent> CreateAgentAsync(
        AgentConfiguration config,
        IAgentChat chat,
        IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
        IReadOnlyList<AgentConfiguration> hierarchyAgents,
        string? modelName = null)
    {
        CurrentModelName = modelName;

        // The composer selection (ThreadComposer.ModelName → modelName / CurrentModelName) always
        // wins. When nothing was selected (headless flows: email routing, notification triage,
        // delegated sub-threads), concrete factories fall back to the agent's ModelTier via
        // ResolveTierModel, then to their provider default.

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
            // No plugins declared → READ-ONLY Mesh tools. Write capability is an explicit
            // grant: declare `plugins: [Mesh]` in the agent definition to get
            // Create/Update/Patch/EditContent/Delete/Move/Copy/Recycle. (This replaces the
            // old description-keyword gating — "description contains create/update/delete" —
            // where rewording an agent's description silently granted or revoked write
            // access. Capability must never hinge on prose.)
            var meshPlugin = new MeshPlugin(Hub, chat);
            tools = tools.Concat(meshPlugin.CreateTools());
        }

        tools = tools.Append(PlanStorageTool.Create(Hub, chat));
        // load_skill: inject a nodeType:Skill node's instructions on demand (found via search nodeType:Skill);
        // a LaunchesSubThread skill runs in its own sub-thread via the generic StartThread launcher.
        tools = tools.Append(SkillTool.Create(Hub, chat));

        // Wrap all tools to restore user access context before invocation.
        // AsyncLocal doesn't flow through the AI framework's streaming + tool invocation,
        // so each tool call must explicitly restore the user identity from the
        // thread's execution context. This is the single injection point for ALL tools.
        var accessService = Hub.ServiceProvider.GetService<AccessService>();
        var wrappedTools = tools.Select(tool => WrapToolWithAccessContext(tool, chat, accessService)).ToList();

        // #321: when a per-round tool-call cap is configured, make the model AWARE of its
        // budget so it wraps up gracefully with a "type continue" hand-off on reaching it.
        // The HARD enforcement is FunctionInvokingChatClient.MaximumIterationsPerRequest (set
        // below); this instruction only makes the limit self-documenting to the model so the
        // graceful final answer reads as a summary rather than an abrupt stop.
        var effectiveInstructions = instructions;
        if (config.MaxToolCallsPerRound is { } capForPrompt && capForPrompt > 0)
            effectiveInstructions +=
                $"\n\n---\nTool-call budget: you may issue at most {capForPrompt} tool calls in this turn. " +
                "If you reach this budget before finishing, stop, briefly summarise what you accomplished, " +
                "and ask the user to reply \"continue\" to run the next batch.";

        var agent = new ChatClientAgent(
            chatClient: chatClient, instructions: effectiveInstructions,
            name: name, description: description,
            tools: wrappedTools, loggerFactory: null, services: null);

        var functionInvoker = agent.ChatClient.GetService<Microsoft.Extensions.AI.FunctionInvokingChatClient>();
        if (functionInvoker != null)
        {
            functionInvoker.AllowConcurrentInvocation = true;
            // #321: per-agent HARD cap on tool-call iterations. Without this,
            // MaximumIterationsPerRequest falls back to the Microsoft.Extensions.AI default
            // (high), so a high-volume agent can issue hundreds of tool calls in a single
            // round before it engages. On reaching the cap the framework strips tools on the
            // final iteration (PrepareOptionsForLastIteration) so the model returns a graceful
            // final answer instead of truncating — it does NOT throw. It only logs "Reached
            // maximum iteration count" internally and surfaces no distinct limit-reached signal
            // to the streaming consumer, so the summary hand-off is driven by the instruction
            // appended above rather than a framework callback.
            if (config.MaxToolCallsPerRound is { } cap && cap > 0)
                functionInvoker.MaximumIterationsPerRequest = cap;
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

    /// <summary>
    /// Standard tools made available to every agent regardless of configuration. The base
    /// implementation returns none; concrete factories override to inject provider-wide tools.
    /// </summary>
    /// <param name="chat">The chat session the tools will operate within.</param>
    /// <returns>The standard tools to add to the agent; empty by default.</returns>
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
            // list_sub_threads: snapshot of this chat's active delegation paths.
            // Only AgentChatClient maintains ActiveDelegationPaths; if the chat
            // is some other IAgentChat (e.g. test stub), skip the optional tool.
            Func<IReadOnlyList<MeshWeaver.AI.Plugins.SubThreadInfo>>? listSubThreads = null;
            Action<string, string>? sendToSubThread = null;
            if (chat is AgentChatClient acc)
            {
                var workspace = Hub.GetWorkspace();
                listSubThreads = () => SnapshotActiveSubThreads(acc, workspace);
                sendToSubThread = (threadPath, message) =>
                    PushMessageToSubThread(workspace, threadPath, message);
            }

            foreach (var tool in DelegationTool.CreateDelegationTools(
                         agentConfig,
                         hierarchyAgents,
                         executeAsync: (agentName, task, context, ct) =>
                             ExecuteDelegationAsync(agentConfig, allAgents, chat, agentName, task, context, ct),
                         listSubThreads: listSubThreads,
                         sendToSubThread: sendToSubThread,
                         delegationEvents: chat.Delegations,
                         workspace: Hub.GetWorkspace(),
                         logger: Logger))
            {
                yield return tool;
            }

            Logger.LogInformation(
                "Created delegation tools for agent {AgentName} with {HierarchyCount} hierarchy agents (list={List}, send={Send})",
                agentConfig.Id, hierarchyAgents.Count, listSubThreads is not null, sendToSubThread is not null);
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
    /// <c>RequestedStatus = Cancelled</c> to the sub-thread + flip our tool call
    /// to <see cref="ToolCallStatus.Cancelled"/>, then yield the partial text
    /// so FCC can carry on.</para>
    /// </summary>
    /// <summary>
    /// Snapshot accessor for the <c>list_sub_threads</c> tool. Reads the
    /// AgentChatClient's <c>ActiveDelegationPaths</c> set (maintained by
    /// EmitDelegationEvent) and enriches each path with a best-effort
    /// status / preview from the workspace cache (synchronous; no awaits).
    /// </summary>
    private static IReadOnlyList<MeshWeaver.AI.Plugins.SubThreadInfo> SnapshotActiveSubThreads(
        AgentChatClient chat, MeshWeaver.Data.IWorkspace workspace)
    {
        var paths = chat.ActiveDelegationPaths;
        if (paths.IsEmpty)
            return Array.Empty<MeshWeaver.AI.Plugins.SubThreadInfo>();

        var result = new List<MeshWeaver.AI.Plugins.SubThreadInfo>(paths.Count);
        foreach (var path in paths)
        {
            // Best-effort agent name extraction from the path tail. The full
            // status / preview / activity enrichment will land in the next
            // refactor pass when the subscriber on the sub-thread node also
            // pushes a snapshot into AgentChatClient for instant tool access.
            // For now: the tool exposes the in-flight list with paths +
            // agent-name parsed from the path slug, which already lets the
            // parent agent decide whether to wait or send a follow-up.
            var lastSegment = path.Split('/').LastOrDefault() ?? path;
            var agentNameGuess = lastSegment.Split('-').FirstOrDefault() ?? "unknown";
            result.Add(new MeshWeaver.AI.Plugins.SubThreadInfo(
                ThreadPath: path,
                AgentName: agentNameGuess,
                Status: "Executing",
                PreviewText: null,
                LastActivity: null));
        }
        return result;
    }

    /// <summary>
    /// Handler for the <c>send_to_sub_thread</c> tool. Writes a follow-up user
    /// message into the sub-thread's pending-messages queue via stream.Update;
    /// the sub-thread's submission watcher picks it up and dispatches a new
    /// round (or the agent's inbox-drain tool absorbs it mid-stream).
    /// </summary>
    private static void PushMessageToSubThread(
        MeshWeaver.Data.IWorkspace workspace, string subThreadPath, string message)
    {
        var userMessage = ThreadInput.CreateUserMessage(
            message,
            createdBy: "parent-agent",
            agentName: null,
            modelName: null,
            contextPath: null,
            attachments: null);
        ThreadInput.AppendUserInput(workspace, subThreadPath, userMessage);
    }

    /// <summary>
    /// Pure reactive sub-thread spawn. No async, no await, no .ToTask().
    /// Returns an IObservable&lt;string&gt; that completes when the sub-thread
    /// has been created. The actual sub-agent execution is driven by the
    /// sub-thread's own submission watcher (it sees PendingUserMessages and
    /// runs the round); the parent's tool-call TCS resolution is handled by
    /// <see cref="MeshWeaver.AI.Plugins.DelegationTool"/>'s reactive Idle
    /// subscription, which reads <c>Thread.Summary</c> when the sub-thread
    /// returns to Idle. No chunk drain, no Channel bridge, no await foreach.
    /// </summary>
    private IObservable<string> ExecuteDelegationAsync(
        AgentConfiguration agentConfig,
        IReadOnlyDictionary<string, ChatClientAgent> allAgents,
        IAgentChat chat,
        string agentName,
        string task,
        string? context,
        CancellationToken cancellationToken)
        => System.Reactive.Linq.Observable.Defer(() =>
        {
            // Resolve target agent (strip path prefix if present).
            var targetId = agentName.Split('/').Last();
            if (!allAgents.TryGetValue(targetId, out _))
                return System.Reactive.Linq.Observable.Return($"Agent '{agentName}' not found");

            var execCtx = chat.ExecutionContext;
            if (execCtx is null)
                return System.Reactive.Linq.Observable.Return("No execution context available for delegation");

            // Depth guard.
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
                return System.Reactive.Linq.Observable.Return(
                    $"Maximum delegation depth reached ({depth}). Handle this task directly.");
            }

            Logger.LogInformation("[Delegation] {Source} → {Target}, depth={Depth}, task={Task}",
                agentConfig.Id, targetId, depth, task.Length > 100 ? task[..97] + "..." : task);

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
            var callId = Guid.NewGuid().ToString("N")[..8];

            Logger.LogInformation(
                "[Delegation:{CallId}] ENTER sub={SubPath} target={Target} parentResp={ParentResp}",
                callId, subThreadPath, targetId, parentMsgPath);

            var meshService = Hub.ServiceProvider.GetRequiredService<IMeshService>();

            // Pure nested-Subscribe pattern (per CLAUDE.md AsynchronousCalls.md):
            // CreateNode → on emission, subscribe to sub-thread stream for the
            // Running→Idle transition → on Idle, emit Terminal event so the
            // parent's tool-call TCS (in DelegationTool's reactive subscription)
            // resolves with Thread.Summary. No await, no .ToTask(), no SelectMany
            // chain that could capture the grain scheduler — Subscribe is the
            // continuation primitive.
            return System.Reactive.Linq.Observable.Create<string>(observer =>
            {
                var workspace = Hub.GetWorkspace();
                var sawRunning = false;

                meshService.CreateNode(subThreadNode).Subscribe(
                    _ =>
                    {
                        Logger.LogInformation(
                            "[Delegation:{CallId}] CREATE_OK sub={Path}", callId, subThreadPath);

                        if (chat is AgentChatClient agentChat)
                        {
                            Logger.LogInformation(
                                "[Delegation:{CallId}] EMIT_DISPATCHED sub={Path}", callId, subThreadPath);
                            agentChat.EmitDelegationEvent(
                                new MeshWeaver.AI.Delegation.DelegationEvent(callId, subThreadPath,
                                    MeshWeaver.AI.Delegation.DelegationLifecycle.Dispatched));

                            // Watch sub-thread for Running→Idle. Same Scan-based
                            // pattern DelegationTool uses for the parent's
                            // TCS resolution — emit Terminal here so the
                            // cancel-watcher + tool-call stamper drop the entry.
                            // Self-disposing watch: the sub-thread sync subscription is released
                            // the moment the sub-thread reaches a terminal state. Without this it
                            // stays subscribed for the life of the PROCESS — one leaked sync/ hub
                            // per delegation, which accumulates until the portal wedges (the atioz
                            // 2026-06-25 wedge: ~1778 leaked sync hubs). See the /storm skill.
                            IDisposable? subThreadWatch = null;
                            subThreadWatch = workspace.GetMeshNodeStream(subThreadPath).Subscribe(
                                node =>
                                {
                                    if (node?.Content is not MeshThread t) return;
                                    if (t.Status is ThreadExecutionStatus.Executing
                                                 or ThreadExecutionStatus.StartingExecution)
                                    {
                                        sawRunning = true;
                                    }
                                    else if (sawRunning && t.Status is ThreadExecutionStatus.Idle
                                                 or ThreadExecutionStatus.Cancelled
                                                 or ThreadExecutionStatus.Done)
                                    {
                                        Logger.LogInformation(
                                            "[Delegation:{CallId}] TERMINAL sub={Path} (Running→Idle)",
                                            callId, subThreadPath);
                                        agentChat.EmitDelegationEvent(
                                            new MeshWeaver.AI.Delegation.DelegationEvent(callId, subThreadPath,
                                                MeshWeaver.AI.Delegation.DelegationLifecycle.Terminal));
                                        // Done watching — release the sync subscription (no leak).
                                        subThreadWatch?.Dispose();
                                    }
                                },
                                ex => Logger.LogWarning(ex,
                                    "[Delegation:{CallId}] sub-thread stream errored", callId));
                        }

                        // No chunks emitted — DelegationTool's reactive
                        // completion path reads Thread.Summary directly on Idle.
                        observer.OnCompleted();
                    },
                    ex =>
                    {
                        Logger.LogWarning(ex, "[Delegation:{CallId}] CREATE_FAIL sub={Path}",
                            callId, subThreadPath);
                        // 🚨 Surface the create failure so the parent's tool call resolves IMMEDIATELY
                        // as an "Error: …" result instead of hanging. Two surfaces for the two waits:
                        //  • production (delegationEvents+workspace wired): emit a Failed lifecycle
                        //    event — the wait gates on Dispatched (never emitted on create-fail) and the
                        //    chunk-aggregate OnCompleted returns EARLY in that path, so without this the
                        //    delegate_to_agent call never resolves (apparent wedge).
                        //  • legacy (no delegationEvents): the OnCompleted chunk-aggregate fallback
                        //    resolves the TCS — prefix "Error:" so ExtractToolResult keys IsSuccess=false.
                        if (chat is AgentChatClient agentChat)
                            agentChat.EmitDelegationEvent(
                                new MeshWeaver.AI.Delegation.DelegationEvent(callId, subThreadPath,
                                    MeshWeaver.AI.Delegation.DelegationLifecycle.Failed, ex.Message));
                        observer.OnNext($"Error: delegation to {targetId} failed to start — {ex.Message}");
                        observer.OnCompleted();
                    });

                return System.Reactive.Disposables.Disposable.Empty;
            });
        });

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

    /// <summary>
    /// Builds the agent's full instruction text with @@references resolved (the async path), then
    /// appends the delegation, handoff, and thread-inspection/summary guidance for the hierarchy.
    /// </summary>
    /// <param name="agentConfig">The agent whose base instructions are resolved and augmented.</param>
    /// <param name="hierarchyAgents">All agents in the hierarchy, used to render the available-delegation/handoff lists.</param>
    /// <param name="chat">The chat session, used to resolve @@references against the live workspace.</param>
    /// <returns>The composed instruction string for the agent.</returns>
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

        // Thread-message inspection + summary contract — appended for every
        // agent so delegation-incapable agents still know how to discover
        // prior responses and how their own response gets stored as a summary.
        var threadInspectionAndSummary =
            """

            **Reading prior thread messages:**
            Use the `search` tool with `nodeType:ThreadMessage` to find conversation cells.
              - One thread: `search "path:{threadPath} scope:descendants nodeType:ThreadMessage"`
              - One sub-thread (delegation child): same shape with the sub-thread's path.
              - Project only the fields you need with `select:` — e.g.
                `search "path:{threadPath} scope:descendants nodeType:ThreadMessage select:text,summary,role,timestamp"`.
              - To read the dedicated summary of a completed sub-thread directly,
                `get "{subThreadPath}"` and read `content.summary` (filled atomically
                with `content.status=Idle`).
            Use `select:summary` when scanning many cells — it returns the one-line
            digest without the verbose `text`, so you can survey a deep thread cheaply.

            **End every response with a <summary> block:**
            At the very end of your response, on its own line, emit:
              `<summary>One- or two-sentence distillation of what you did or decided.</summary>`
            The framework strips this from what the user sees and stores it as the
            dedicated summary on both the response message and the thread. When a
            parent agent delegated to you, this summary IS the tool-call result it
            receives back — not your verbose response. Be tight and outcome-focused.

            """;

        if (!hasDelegations && !hasHandoffs && !hasHierarchyAgents && !agentConfig.IsDefault)
        {
            return baseInstructions + threadInspectionAndSummary;
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

        result += threadInspectionAndSummary;

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
