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
    public abstract int DisplayOrder { get; }

    /// <summary>
    /// Creates a ChatClientAgent for the given configuration.
    /// </summary>
    public Task<ChatClientAgent> CreateAgentAsync(
        AgentConfiguration config,
        IAgentChat chat,
        IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
        IReadOnlyList<AgentConfiguration> hierarchyAgents,
        string? modelName = null)
    {
        CurrentModelName = modelName;

        var name = config.Id;
        var description = config.Description ?? string.Empty;
        var instructions = GetAgentInstructions(config, hierarchyAgents);

        // Create a chat client for this agent using the derived class implementation
        var chatClient = CreateChatClient(config);

        // Get tools for this agent, passing the chat instance so plugins can access context
        var tools = GetToolsForAgent(config, chat, existingAgents, hierarchyAgents).ToArray();

        // Add MeshPlugin tools for agents that need mesh operations
        var meshPlugin = new MeshPlugin(Hub, chat);
        tools = tools.Concat(meshPlugin.CreateTools()).ToArray();

        // Create ChatClientAgent with all parameters
        var agent = new ChatClientAgent(
            chatClient: chatClient,
            instructions: instructions,
            name: name,
            description: description,
            tools: tools,
            loggerFactory: null,
            services: null
        );

        return Task.FromResult(agent);
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
        var logger = Hub.ServiceProvider.GetService<ILogger<ChatPlugin>>();
        return new ChatPlugin(chat, logger).CreateTools();
    }

    /// <summary>
    /// Creates delegation tools for agents that can delegate to other agents.
    /// Uses a unified Delegate tool that includes all available agents in its description.
    /// </summary>
    protected virtual IEnumerable<AITool> GetAgentTools(
        AgentConfiguration agentConfig,
        IAgentChat chat,
        IReadOnlyDictionary<string, ChatClientAgent> allAgents,
        IReadOnlyList<AgentConfiguration> hierarchyAgents)
    {
        var hasDelegations = agentConfig.Delegations is { Count: > 0 };
        var hasHierarchyAgents = hierarchyAgents.Count > 1;

        if (!hasDelegations && !hasHierarchyAgents && !agentConfig.IsDefault)
        {
            yield break;
        }

        #pragma warning disable CS0618 // Type or member is obsolete - migration to DelegationTool.CreateUnifiedDelegationTool requires executeAsync implementation
        var delegationTool = ChatPlugin.CreateUnifiedDelegationTool(
            agentConfig,
            hierarchyAgents,
            Logger);
        #pragma warning restore CS0618

        Logger.LogInformation("Created unified delegation tool for agent {AgentName} with {HierarchyCount} hierarchy agents",
            agentConfig.Id, hierarchyAgents.Count);

        yield return delegationTool;
    }

    protected string GetAgentInstructions(AgentConfiguration agentConfig, IReadOnlyList<AgentConfiguration> hierarchyAgents)
    {
        var baseInstructions = agentConfig.Instructions ?? string.Empty;

        var hasDelegations = agentConfig.Delegations is { Count: > 0 };
        var hasHierarchyAgents = hierarchyAgents.Count > 1;

        if (!hasDelegations && !hasHierarchyAgents && !agentConfig.IsDefault)
        {
            return baseInstructions;
        }

        var agentList = new List<string>();

        if (agentConfig.Delegations != null)
        {
            foreach (var d in agentConfig.Delegations)
            {
                var agentId = d.AgentPath.Split('/').Last();
                agentList.Add($"- {agentId}: {d.Instructions}");
            }
        }

        var listedIds = agentConfig.Delegations?.Select(d => d.AgentPath.Split('/').Last()).ToHashSet()
            ?? new HashSet<string>();

        foreach (var agent in hierarchyAgents.Where(a => a.Id != agentConfig.Id && !listedIds.Contains(a.Id)))
        {
            agentList.Add($"- {agent.Id}: {agent.Description ?? "Agent in hierarchy"}");
        }

        if (agentList.Count == 0)
        {
            return baseInstructions;
        }

        var agentListStr = string.Join('\n', agentList);

        var delegationGuidelines =
            $$$"""

               **Agent Delegation:**
               You have access to a unified Delegate tool to route requests to specialized agents.
               Use this when the request matches another agent's expertise or when you need to escalate.

               **Available Agents:**
               {{{agentListStr}}}

               **How to delegate:**
               1. Identify which specialized agent can best handle the user's request
               2. Call the Delegate tool with the agent name and your message
               3. The delegated agent will handle the request and respond directly

               **Important:**
               - After calling Delegate, do not provide additional output
               - Choose the most appropriate agent based on their specialization
               - For escalation (when you can't handle something), delegate to an agent higher in the hierarchy

               """;

        return baseInstructions + delegationGuidelines;
    }
}
