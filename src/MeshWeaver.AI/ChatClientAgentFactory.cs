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

        // Deterministically pre-compute the sub-thread path (GenerateSpeakingId
        // is pure of the task text). The thread-hub handler will derive the SAME
        // path inside BuildThreadWithMessages — so we can stamp it on the
        // parent's tool-call entry RIGHT NOW via the Dispatched event without
        // waiting for the round-trip to the handler.
        var subThreadPath = $"{parentMsgPath}/{MeshWeaver.AI.ThreadNodeType.GenerateSpeakingId(task)}";
        var callId = Guid.NewGuid().ToString("N")[..8];

        // Emit Dispatched onto chat.Delegations. AgentChatClient.EmitDelegationEvent
        // also updates the ActiveDelegationPaths snapshot that the cancel watcher
        // and the streaming-loop's stamp pass read. Single source of truth.
        if (chat is AgentChatClient agentChat)
            agentChat.EmitDelegationEvent(
                new MeshWeaver.AI.Delegation.DelegationEvent(callId, subThreadPath,
                    MeshWeaver.AI.Delegation.DelegationLifecycle.Dispatched));

        // Resolve the _Exec hub we're running on (FCC dispatches us from
        // ExecuteMessageAsync, which runs on _Exec) and its DelegationRegistry.
        // Hub here is the FACTORY's hub (typically the mesh hub) — that's NOT
        // _Exec. Get _Exec from the threadHub's hosted-hub cache.
        var threadHubAddress = new Address(threadPath);
        var execHubAddress = new Address($"{threadPath}/_Exec");
        var threadHub = Hub.GetHostedHub(threadHubAddress, HostedHubCreation.Never)
            ?? throw new InvalidOperationException(
                $"Thread hub at {threadHubAddress} not found for delegation");
        var execHub = threadHub.GetHostedHub(execHubAddress, HostedHubCreation.Never)
            ?? throw new InvalidOperationException(
                $"_Exec hub at {execHubAddress} not found for delegation");
        var registry = execHub.ServiceProvider.GetRequiredService<MeshWeaver.AI.Delegation.DelegationRegistry>();

        // Per-CallId channel that drives this IAsyncEnumerable. Writer is fed
        // exclusively by HandleSubThreadStateChanged on _Exec's action block —
        // single-writer guarantee without locks.
        var channel = System.Threading.Channels.Channel.CreateUnbounded<MeshWeaver.AI.Delegation.DelegationFrame>(
            new System.Threading.Channels.UnboundedChannelOptions
            { SingleReader = true, SingleWriter = false });

        var entry = new MeshWeaver.AI.Delegation.DelegationEntry
        {
            CallId = callId,
            SubThreadPath = subThreadPath,
            ResponseMsgId = "", // populated by HandleDelegationSubThreadCreated when the handler computes it
            Writer = channel.Writer,
        };
        registry.Active[callId] = entry;

        // Best-effort cleanup if the caller (FCC turn) gets cancelled mid-flight.
        using var cleanup = cancellationToken.Register(() =>
        {
            if (registry.Active.TryRemove(callId, out var e))
            {
                e.Subscription?.Dispose();
                e.Writer.TryComplete();
            }
        });

        // Post the lifecycle kickoff. The thread-hub handler sequences the three
        // CreateNode observables (.Concat), then posts DelegationSubThreadCreated
        // back to _Exec, which installs THE single observation subscription.
        // From here, every state change flows through _Exec's action block as
        // a SubThreadStateChanged message — no race, no rogue subscriptions on
        // the mesh-hub scheduler.
        threadHub.Post(
            new MeshWeaver.AI.Delegation.CreateDelegationSubThread(
                CallId: callId,
                ParentMsgPath: parentMsgPath,
                TargetAgentId: targetId,
                Task: task,
                MainEntityPath: mainEntityPath));

        // Drain frames until terminal. Each Delta is yielded to FCC's
        // FunctionResultContent accumulator — sub-thread streaming text reaches
        // the parent agent intact. The terminal frame carries the final
        // ThreadMessageStatus / error message.
        ThreadMessageStatus? finalStatus = null;
        string? terminalError = null;
        await foreach (var frame in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!string.IsNullOrEmpty(frame.Delta))
                yield return frame.Delta;
            if (frame.Terminal)
            {
                finalStatus = frame.FinalStatus;
                terminalError = frame.ErrorMessage;
                break;
            }
        }

        // Emit Terminal onto chat.Delegations so subscribers (cancel watcher,
        // tool-call stamper) can drop their per-CallId state.
        if (chat is AgentChatClient agentChat2)
            agentChat2.EmitDelegationEvent(
                new MeshWeaver.AI.Delegation.DelegationEvent(callId, subThreadPath,
                    MeshWeaver.AI.Delegation.DelegationLifecycle.Terminal));

        Logger.LogInformation(
            "[Delegation] Sub-thread {Path} settled (status={Status}, error={Error})",
            subThreadPath, finalStatus, terminalError ?? "(none)");

        // Terminal marker only on error/cancel. On success the deltas already
        // delivered the full sub-thread text to FCC's FunctionResultContent.
        if (terminalError is not null)
            yield return $"\n[Delegation failed: {terminalError}]";
        else if (cancellationToken.IsCancellationRequested
                 || finalStatus == ThreadMessageStatus.Cancelled)
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
