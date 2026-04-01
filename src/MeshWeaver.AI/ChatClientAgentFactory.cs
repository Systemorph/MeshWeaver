using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using MeshWeaver.AI.Plugins;
using MeshWeaver.Data;
using MeshWeaver.Graph;
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
            functionInvoker.AllowConcurrentInvocation = true;

        return agent;
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
                executeAsync: (agentName, task, context, cancellationToken) =>
                {
                    // Resolve the target agent by name (strip path prefix if present)
                    var targetId = agentName.Split('/').Last();
                    if (!allAgents.TryGetValue(targetId, out var targetAgent))
                    {
                        return Task.FromResult(new DelegationResult
                        {
                            AgentName = agentName,
                            Task = task,
                            Result = $"Agent '{agentName}' not found",
                            Success = false
                        });
                    }

                    var execCtx = chat.ExecutionContext;
                    var userIdentity = execCtx?.UserAccessContext?.ObjectId ?? "(no-user)";

                    // Guard: limit delegation depth to prevent infinite recursion
                    var depth = execCtx?.ThreadPath?.Split("/_Thread/").Length - 1 ?? 0;
                    if (depth >= 3)
                    {
                        Logger.LogWarning("[Delegation] Max delegation depth ({Depth}) reached for {Source} → {Target}",
                            depth, agentConfig.Id, targetId);
                        return Task.FromResult(new DelegationResult
                        {
                            AgentName = targetId, Task = task,
                            Result = $"Maximum delegation depth reached ({depth}). Cannot delegate further — handle the task directly.",
                            Success = false
                        });
                    }

                    Logger.LogInformation(
                        "[Delegation] {Source} → {Target}, user={User}, depth={Depth}, task={Task}",
                        agentConfig.Id, targetId, userIdentity, depth, task.Length > 100 ? task[..97] + "..." : task);

                    if (execCtx == null)
                    {
                        return Task.FromResult(new DelegationResult
                        {
                            AgentName = targetId,
                            Task = task,
                            Result = "No execution context available for delegation",
                            Success = false
                        });
                    }

                    // TCS completed by subscription callbacks — framework awaits this, not our code
                    var tcs = new TaskCompletionSource<DelegationResult>();
                    var meshService = Hub.ServiceProvider.GetRequiredService<IMeshService>();
                    var workspace = Hub.GetWorkspace();

                    // Access context is restored by WrapToolWithAccessContext — no need to set it here.

                    var subThreadId = ThreadNodeType.GenerateSpeakingId(task);
                    var parentMsgPath = $"{execCtx.ThreadPath}/{execCtx.ResponseMessageId}";
                    var subThreadPath = $"{parentMsgPath}/{subThreadId}";

                    // 1. Create sub-thread node (Observable — no await)
                    var mainEntityPath = context ?? execCtx.ContextPath ?? execCtx.ThreadPath;
                    var subThreadNode = new MeshNode(subThreadId, parentMsgPath)
                    {
                        Name = task.Length > 60 ? task[..57] + "..." : task,
                        NodeType = ThreadNodeType.NodeType,
                        MainNode = mainEntityPath,
                        Content = new MeshThread()
                    };

                    // Set delegation path and notify — the streaming loop is blocked
                    // during tool execution, so the throttle never fires. The callback
                    // pushes the tool call with DelegationPath immediately.
                    chat.LastDelegationPath = subThreadPath;
                    chat.UpdateDelegationStatus?.Invoke($"Delegating to {targetId}...");

                    meshService.CreateNode(subThreadNode).Subscribe(
                        _ =>
                        {
                            Logger.LogInformation("[Delegation] Created sub-thread at {Path}", subThreadPath);

                            // 2. Completion is notified via a second SubmitMessageResponse
                            // with Status=ExecutionCompleted, posted by ThreadExecution when done.

                            // 3. Submit message (Post + RegisterCallback — no AwaitResponse)
                            var delivery = Hub.Post(new SubmitMessageRequest
                            {
                                ThreadPath = subThreadPath,
                                UserMessageText = task,
                                AgentName = targetId,
                                ContextPath = context ?? execCtx.ContextPath ?? execCtx.ThreadPath
                            }, o =>
                            {
                                o = o.WithTarget(new Address(subThreadPath));
                                if (execCtx.UserAccessContext != null)
                                    o = o.WithAccessContext(execCtx.UserAccessContext);
                                return o;
                            });

                            if (delivery == null)
                            {
                                Logger.LogWarning("[Delegation] SubmitMessageRequest post returned null for {Path}", subThreadPath);
                                tcs.TrySetResult(new DelegationResult
                                {
                                    AgentName = targetId, Task = task,
                                    Result = $"Failed to submit message to sub-thread {subThreadPath}", Success = false
                                });
                                return;
                            }

                            Hub.RegisterCallback((IMessageDelivery)delivery, response =>
                            {
                                    if (response is IMessageDelivery<SubmitMessageResponse> sr)
                                    {
                                        var msg = sr.Message;
                                        if (!msg.Success)
                                        {
                                            Logger.LogWarning("[Delegation] Submit failed: {Error}", msg.Error);
                                            tcs.TrySetResult(new DelegationResult
                                            {
                                                AgentName = targetId, Task = task,
                                                Result = $"Submit failed: {msg.Error}", Success = false
                                            });
                                        }
                                        else if (msg.Status != SubmitMessageStatus.CellsCreated)
                                        {
                                            // Execution completed/cancelled/failed — resolve delegation
                                            Logger.LogInformation("[Delegation] Child finished: {Path}, status={Status}, textLen={TextLen}",
                                                subThreadPath, msg.Status, msg.ResponseText?.Length ?? 0);
                                            tcs.TrySetResult(new DelegationResult
                                            {
                                                AgentName = targetId, Task = task,
                                                Result = msg.ResponseText ?? $"Delegation to {targetId} completed.",
                                                Success = msg.Status == SubmitMessageStatus.ExecutionCompleted,
                                                ThreadId = subThreadPath
                                            });
                                        }
                                        // CellsCreated = initial response, keep waiting for completion
                                    }
                                    return response;
                                });
                        },
                        error =>
                        {
                            Logger.LogWarning(error, "[Delegation] Failed to create sub-thread for {Target}", targetId);
                            tcs.TrySetResult(new DelegationResult
                            {
                                AgentName = targetId, Task = task,
                                Result = $"Node creation failed: {error.Message}", Success = false
                            });
                        });

                    // Register cancellation to prevent infinite hang if sub-thread routing fails
                    cancellationToken.Register(() =>
                    {
                        tcs.TrySetResult(new DelegationResult
                        {
                            AgentName = targetId, Task = task,
                            Result = $"Delegation to {targetId} was cancelled.",
                            Success = false
                        });
                    });

                    return tcs.Task;
                },
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
