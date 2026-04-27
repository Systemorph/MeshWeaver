using System.Collections.Immutable;
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
    /// Dispatches a sub-thread and yields its streaming text deltas as <see cref="IAsyncEnumerable{string}"/>.
    ///
    /// The sub-thread is created fire-and-forget via <c>IMeshService.CreateNode</c> (no await on
    /// completion). Its response-message cell is observed through a workspace remote stream; each
    /// incremental delta is yielded up to the <see cref="FunctionInvokingChatClient"/>, and via
    /// that — through the parent agent's streaming response — into the parent's response bubble.
    ///
    /// No <see cref="Task{string}"/>, no <see cref="TaskCompletionSource{T}"/>, no
    /// <c>ObserveQuery</c>. The only awaits here are on the channel reader which drains on
    /// cancellation or on the sub-thread's CompletedAt flip — neither touches the hub scheduler
    /// (both run on the Task.Run thread pool).
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

        yield return $"\n\n**Delegating to {targetId}…**\n\n";

        // Open a channel fed by the sub-thread's response-cell remote stream. We yield
        // each text delta as it arrives (computed against lastText so we never double-emit).
        var channel = System.Threading.Channels.Channel.CreateUnbounded<string>(
            new System.Threading.Channels.UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

        var workspace = Hub.GetWorkspace();
        var lastText = "";
        var subscription = workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
                new Address(responsePath), new MeshNodeReference())
            ?.Subscribe(
                change =>
                {
                    var msg = change.Value?.Content as ThreadMessage;
                    if (msg == null) return;
                    var current = msg.Text ?? "";
                    if (current.Length > lastText.Length)
                    {
                        var delta = current[lastText.Length..];
                        lastText = current;
                        channel.Writer.TryWrite(delta);
                    }
                    if (msg.CompletedAt is not null)
                        channel.Writer.TryComplete();
                },
                ex => channel.Writer.TryComplete(ex),
                () => channel.Writer.TryComplete());

        // Safety timeout so a never-completing sub-thread can't pin this iterator forever.
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            await foreach (var delta in channel.Reader.ReadAllAsync(linked.Token))
            {
                yield return delta;
            }
        }
        finally
        {
            subscription?.Dispose();
            Logger.LogInformation("[Delegation] Stream closed for sub-thread {Path}", subThreadPath);
        }
    }

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
