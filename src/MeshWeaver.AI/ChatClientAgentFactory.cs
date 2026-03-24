using System.Text;
using MeshWeaver.AI.Plugins;
using MeshWeaver.Data;
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
    public async Task<ChatClientAgent> CreateAgentAsync(
        AgentConfiguration config,
        IAgentChat chat,
        IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
        IReadOnlyList<AgentConfiguration> hierarchyAgents,
        string? modelName = null)
    {
        CurrentModelName = modelName;

        // Resolve model tier to concrete model name if PreferredModel isn't set
        if (string.IsNullOrEmpty(config.PreferredModel) && !string.IsNullOrEmpty(config.ModelTier))
        {
            var tierConfig = Hub.ServiceProvider.GetService<IOptions<ModelTierConfiguration>>()?.Value;
            var resolvedModel = tierConfig?.Resolve(config.ModelTier);
            if (!string.IsNullOrEmpty(resolvedModel))
                config = config with { PreferredModel = resolvedModel };
        }

        var name = config.Id;
        var description = config.Description ?? string.Empty;
        var instructions = await GetAgentInstructionsAsync(config, hierarchyAgents, chat);

        // Create a chat client for this agent using the derived class implementation
        var chatClient = CreateChatClient(config);

        // Get delegation/handoff tools
        var agentTools = GetAgentTools(config, chat, existingAgents, hierarchyAgents);
        IEnumerable<AITool> tools = agentTools;

        if (config.Plugins is { Count: > 0 })
        {
            // Explicit plugin mode: only declared plugins are loaded
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
            // Legacy mode: Mesh tools (backward compatibility)
            var meshPlugin = new MeshPlugin(Hub, chat);
            var needsWriteTools = description.Contains("create", StringComparison.OrdinalIgnoreCase)
                || description.Contains("update", StringComparison.OrdinalIgnoreCase)
                || description.Contains("delete", StringComparison.OrdinalIgnoreCase);
            tools = tools.Concat(needsWriteTools ? meshPlugin.CreateAllTools() : meshPlugin.CreateTools());
        }

        // Add store_plan tool to all agents
        tools = tools.Append(PlanStorageTool.Create(Hub, chat));

        // Create ChatClientAgent with all parameters
        var agent = new ChatClientAgent(
            chatClient: chatClient,
            instructions: instructions,
            name: name,
            description: description,
            tools: tools.ToList(),
            loggerFactory: null,
            services: null
        );

        // Enable parallel tool execution — when the LLM returns multiple tool calls
        // in a single response turn, they will be invoked concurrently
        var functionInvoker = agent.ChatClient.GetService<Microsoft.Extensions.AI.FunctionInvokingChatClient>();
        if (functionInvoker != null)
            functionInvoker.AllowConcurrentInvocation = true;

        return agent;
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
                executeAsync: async (agentName, task, cancellationToken) =>
                {
                    // Resolve the target agent by name (strip path prefix if present)
                    var targetId = agentName.Split('/').Last();
                    if (!allAgents.TryGetValue(targetId, out var targetAgent))
                    {
                        return new DelegationResult
                        {
                            AgentName = agentName,
                            Task = task,
                            Result = $"Agent '{agentName}' not found",
                            Success = false
                        };
                    }

                    var execCtx = chat.ExecutionContext;
                    var userIdentity = execCtx?.UserAccessContext?.ObjectId ?? "(no-user)";
                    Logger.LogInformation(
                        "[Delegation] {Source} → {Target}, user={User}, task={Task}",
                        agentConfig.Id, targetId, userIdentity, task.Length > 100 ? task[..97] + "..." : task);

                    // Create sub-thread and submit via SubmitMessageRequest (same as regular threads)
                    string? subThreadPath = null;
                    if (execCtx != null)
                    {
                        try
                        {
                            var meshService = Hub.ServiceProvider.GetRequiredService<IMeshService>();
                            var subThreadId = ThreadNodeType.GenerateSpeakingId(task);
                            var parentMsgPath = $"{execCtx.ThreadPath}/{execCtx.ResponseMessageId}";
                            subThreadPath = $"{parentMsgPath}/{subThreadId}";

                            // 1. Create the sub-thread node (directly under the message, no extra _Thread)
                            var subThreadNode = new MeshNode(subThreadId, parentMsgPath)
                            {
                                Name = task.Length > 60 ? task[..57] + "..." : task,
                                NodeType = ThreadNodeType.NodeType,
                                Content = new MeshThread { ParentPath = execCtx.ThreadPath }
                            };
                            await meshService.CreateNodeAsync(subThreadNode, cancellationToken);
                            chat.LastDelegationPath = subThreadPath;
                            Logger.LogInformation("Created delegation sub-thread at {Path}", subThreadPath);

                            // 2. Submit message to sub-thread via SubmitMessageRequest
                            //    This goes through the full ThreadExecution pipeline:
                            //    creates input/output cells, streams agent response, persists everything.
                            var submitResponse = await Hub.AwaitResponse(
                                new SubmitMessageRequest
                                {
                                    ThreadPath = subThreadPath,
                                    UserMessageText = task,
                                    AgentName = targetId,
                                    ContextPath = execCtx.ThreadPath
                                },
                                o =>
                                {
                                    o = o.WithTarget(new Address(subThreadPath));
                                    // Forward the original user's AccessContext so the sub-thread
                                    // runs under the correct user identity for permission checks
                                    if (execCtx.UserAccessContext != null)
                                        o = o.WithAccessContext(execCtx.UserAccessContext);
                                    return o;
                                },
                                cancellationToken);

                            if (!submitResponse.Message.Success)
                            {
                                Logger.LogWarning("Delegation submit failed for {Path}: {Error}",
                                    subThreadPath, submitResponse.Message.Error);
                            }
                            else
                            {
                                // 3. Wait for execution to complete by polling the sub-thread messages.
                                //    Forward the sub-agent's ExecutionStatus and text preview to the parent.
                                var completed = false;
                                var pollTimeout = DateTime.UtcNow.AddMinutes(5);
                                while (!completed && DateTime.UtcNow < pollTimeout && !cancellationToken.IsCancellationRequested)
                                {
                                    await Task.Delay(500, cancellationToken);

                                    // Read sub-agent's current state
                                    await foreach (var node in meshService.QueryAsync<MeshNode>(
                                        $"namespace:{subThreadPath} nodeType:{ThreadMessageNodeType.NodeType}"))
                                    {
                                        if (node.Content is ThreadMessage tmsg && tmsg.Role == "assistant")
                                        {
                                            if (!tmsg.IsExecuting)
                                            {
                                                completed = true;
                                                break;
                                            }

                                            // Forward sub-agent's live status to parent bubble
                                            if (!string.IsNullOrEmpty(tmsg.ExecutionStatus))
                                            {
                                                chat.UpdateDelegationStatus?.Invoke($"{targetId}: {tmsg.ExecutionStatus}");
                                            }
                                            else if (!string.IsNullOrEmpty(tmsg.Text))
                                            {
                                                // Show last ~100 chars of response text as preview
                                                var preview = tmsg.Text.Length > 100
                                                    ? "..." + tmsg.Text[^100..]
                                                    : tmsg.Text;
                                                chat.UpdateDelegationStatus?.Invoke($"{targetId}: {preview}");
                                            }
                                            else
                                            {
                                                chat.UpdateDelegationStatus?.Invoke($"{targetId}: Processing...");
                                            }
                                        }
                                    }
                                }
                            }

                            // 4. Read the result from the sub-thread's assistant message
                            var resultText = "";
                            await foreach (var node in meshService.QueryAsync<MeshNode>(
                                $"namespace:{subThreadPath} nodeType:{ThreadMessageNodeType.NodeType}"))
                            {
                                if (node.Content is ThreadMessage tmsg && tmsg.Role == "assistant")
                                {
                                    resultText = tmsg.Text ?? "";
                                    break;
                                }
                            }

                            Logger.LogInformation("Delegation to {Target} completed via sub-thread, result length: {Length}",
                                targetId, resultText.Length);

                            return new DelegationResult
                            {
                                AgentName = targetId,
                                Task = task,
                                Result = resultText,
                                Success = true,
                                ThreadId = subThreadPath
                            };
                        }
                        catch (Exception ex)
                        {
                            Logger.LogWarning(ex, "Failed delegation via sub-thread for {Target}", targetId);
                        }
                    }

                    // Fallback: run in-memory if no execution context or sub-thread creation failed
                    Logger.LogInformation("Delegation to {Target}: running in-memory (no sub-thread)", targetId);
                    var session = await targetAgent.CreateSessionAsync();
                    var resultBuilder = new StringBuilder();

                    await foreach (var update in targetAgent.RunStreamingAsync(task, session, cancellationToken: cancellationToken))
                    {
                        foreach (var content in update.Contents)
                        {
                            if (content is FunctionCallContent subCall)
                            {
                                chat.UpdateDelegationStatus?.Invoke($"{targetId}: {ToolStatusFormatter.Format(subCall)}");
                            }
                        }
                        if (!string.IsNullOrEmpty(update.Text))
                            resultBuilder.Append(update.Text);
                    }

                    return new DelegationResult
                    {
                        AgentName = targetId,
                        Task = task,
                        Result = resultBuilder.ToString(),
                        Success = true,
                        ThreadId = subThreadPath
                    };
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
            "Collaboration" => new CollaborationPlugin(Hub, chat).CreateTools(),
            _ => Hub.ServiceProvider.GetServices<IAgentPlugin>()
                    .FirstOrDefault(p => string.Equals(p.Name, pluginRef.Name, StringComparison.OrdinalIgnoreCase))
                    ?.CreateTools()
        };

        if (allTools == null)
            return null;

        // Apply method filtering if specified
        if (pluginRef.Methods is { Count: > 0 })
        {
            var methodSet = new HashSet<string>(pluginRef.Methods, StringComparer.OrdinalIgnoreCase);
            return allTools.Where(t => methodSet.Contains(t.Name));
        }

        return allTools;
    }

    protected async Task<string> GetAgentInstructionsAsync(AgentConfiguration agentConfig, IReadOnlyList<AgentConfiguration> hierarchyAgents, IAgentChat chat)
    {
        var baseInstructions = agentConfig.Instructions ?? string.Empty;

        // Resolve @@ references in agent instructions (e.g., @@Agent/ToolsReference)
        baseInstructions = await InlineReferenceResolver.ResolveAsync(baseInstructions, Hub, chat);

        var hasDelegations = agentConfig.Delegations is { Count: > 0 };
        var hasHandoffs = agentConfig.Handoffs is { Count: > 0 };
        var hasHierarchyAgents = hierarchyAgents.Count > 1;

        if (!hasDelegations && !hasHandoffs && !hasHierarchyAgents && !agentConfig.IsDefault)
        {
            return baseInstructions;
        }

        var result = baseInstructions;

        // Delegation guidelines
        var delegationList = new List<string>();

        if (agentConfig.Delegations != null)
        {
            foreach (var d in agentConfig.Delegations)
            {
                var agentId = d.AgentPath.Split('/').Last();
                delegationList.Add($"- {agentId}: {d.Instructions}");
            }
        }

        var listedIds = agentConfig.Delegations?.Select(d => d.AgentPath.Split('/').Last()).ToHashSet()
            ?? new HashSet<string>();
        var handoffIds = agentConfig.Handoffs?.Select(h => h.AgentPath.Split('/').Last()).ToHashSet()
            ?? new HashSet<string>();

        foreach (var agent in hierarchyAgents.Where(a => a.Id != agentConfig.Id && !listedIds.Contains(a.Id) && !handoffIds.Contains(a.Id)))
        {
            delegationList.Add($"- {agent.Id}: {agent.Description ?? "Agent in hierarchy"}");
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
            var handoffList = new List<string>();
            foreach (var h in agentConfig.Handoffs!)
            {
                var agentId = h.AgentPath.Split('/').Last();
                handoffList.Add($"- {agentId}: {h.Instructions}");
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
