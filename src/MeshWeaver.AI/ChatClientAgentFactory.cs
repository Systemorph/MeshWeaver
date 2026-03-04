using MeshWeaver.AI.Plugins;
using MeshWeaver.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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

                    Logger.LogInformation("Executing delegation from {Source} to {Target}: {Task}",
                        agentConfig.Id, targetId, task);

                    // Create an isolated session for the target agent
                    var session = await targetAgent.CreateSessionAsync();

                    // Run the target agent to completion
                    var response = await targetAgent.RunAsync(task, session, cancellationToken: cancellationToken);

                    // Extract text from the response messages
                    var resultText = string.Join("\n", response.Messages
                        .Where(m => m.Role == ChatRole.Assistant)
                        .SelectMany(m => m.Contents)
                        .OfType<TextContent>()
                        .Select(t => t.Text)
                        .Where(t => !string.IsNullOrEmpty(t)));

                    Logger.LogInformation("Delegation to {Target} completed, result length: {Length}",
                        targetId, resultText.Length);

                    return new DelegationResult
                    {
                        AgentName = targetId,
                        Task = task,
                        Result = resultText,
                        Success = true
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
