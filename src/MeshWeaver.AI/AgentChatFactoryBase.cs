using MeshWeaver.AI.Persistence;
using MeshWeaver.AI.Plugins;
using MeshWeaver.AI.Services;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI;

public abstract class AgentChatFactoryBase : IAgentChatFactory
{
    protected readonly IMessageHub Hub;
    protected readonly IAgentResolver AgentResolver;

    /// <summary>
    /// The current model name being used for chat creation
    /// </summary>
    protected string? CurrentModelName { get; private set; }

    /// <summary>
    /// The current context path for agent resolution
    /// </summary>
    protected string? CurrentContextPath { get; private set; }

    /// <summary>
    /// All agents in the current hierarchy, ordered by depth (closest first).
    /// Used for building unified delegation tools.
    /// </summary>
    protected IReadOnlyList<AgentConfiguration> HierarchyAgents { get; private set; } = Array.Empty<AgentConfiguration>();

    protected AgentChatFactoryBase(IMessageHub hub, IAgentResolver agentResolver)
    {
        this.Hub = hub;
        this.AgentResolver = agentResolver;
        Logger = hub.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(GetType());
    }

    protected ILogger Logger { get; }

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

    public virtual Task<IAgentChat> CreateAsync()
    {
        // Use default model (first in the list)
        var defaultModel = Models.FirstOrDefault();
        return CreateAsync(defaultModel ?? string.Empty);
    }

    public virtual Task<IAgentChat> CreateAsync(string modelName)
    {
        return CreateAsync(modelName, null);
    }

    public virtual async Task<IAgentChat> CreateAsync(string modelName, string? contextPath)
    {
        CurrentModelName = modelName;
        CurrentContextPath = contextPath;

        // Load agents from graph using hierarchical resolution
        var agentConfigs = await AgentResolver.GetAgentsForContextAsync(contextPath);

        // Load hierarchy agents ordered by depth (closest first) for unified delegation
        HierarchyAgents = await AgentResolver.GetHierarchyAgentsAsync(contextPath);

        var existingAgents = await GetExistingAgentsAsync();

        // Create AgentChatClient first so it can be passed to GetTools()
        var chatClient = new AgentChatClient(agentConfigs, AgentResolver, Hub.ServiceProvider);

        var agentsByName = existingAgents.ToDictionary(GetAgentName);
        var createdAgents = new Dictionary<string, ChatClientAgent>();

        // Order agents: non-delegating agents first, then delegating agents, then default agent last
        var orderedAgents = OrderAgentsForCreation(agentConfigs);

        // First pass: Create all agents in order without delegation tools
        foreach (var agentConfig in orderedAgents)
        {
            var existingAgent = agentsByName.GetValueOrDefault(agentConfig.Id);
            var agent = await CreateOrUpdateAgentAsync(
                agentConfig,
                existingAgent,
                chatClient,
                createdAgents); // Pass current dictionary - delegating agents get tools for agents created before them

            createdAgents[agentConfig.Id] = agent;
            chatClient.AddAgent(agentConfig.Id, agent);
        }

        // Second pass: Update agents that have cyclic dependencies
        // Find agents that delegate to each other
        var cyclicAgents = FindCyclicDelegations(agentConfigs);

        foreach (var agentConfig in cyclicAgents)
        {
            var existingAgent = agentsByName.GetValueOrDefault(agentConfig.Id);
            var updatedAgent = await CreateOrUpdateAgentAsync(
                agentConfig,
                existingAgent,
                chatClient,
                createdAgents); // Now all agents exist, so cyclic dependencies can be resolved

            // Replace the agent in the chat client
            createdAgents[agentConfig.Id] = updatedAgent;
            chatClient.AddAgent(agentConfig.Id, updatedAgent);

            Logger.LogInformation("Updated agent {AgentName} with cyclic delegation tools",
                agentConfig.Id);
        }

        return chatClient;
    }

    /// <summary>
    /// Orders agents for creation: non-delegating first, delegating second, default agent last
    /// </summary>
    private IEnumerable<AgentConfiguration> OrderAgentsForCreation(IEnumerable<AgentConfiguration> agents)
    {
        var agentList = agents.ToList();

        // 1. Non-delegating agents (no Delegations, not IsDefault)
        var nonDelegating = agentList
            .Where(a => (a.Delegations == null || a.Delegations.Count == 0) && !a.IsDefault);

        // 2. Delegating agents (has Delegations but not IsDefault)
        var delegating = agentList
            .Where(a => a.Delegations is { Count: > 0 } && !a.IsDefault);

        // 3. Default agent (has IsDefault)
        var defaultAgent = agentList
            .Where(a => a.IsDefault);

        return nonDelegating.Concat(delegating).Concat(defaultAgent);
    }

    /// <summary>
    /// Finds agents that have cyclic delegations (agents that delegate to each other)
    /// </summary>
    private IEnumerable<AgentConfiguration> FindCyclicDelegations(IEnumerable<AgentConfiguration> agents)
    {
        var delegatingAgents = agents.Where(a => a.Delegations is { Count: > 0 }).ToList();
        var cyclicAgents = new HashSet<string>();

        foreach (var agent in delegatingAgents)
        {
            var delegatedAgentPaths = agent.Delegations!.Select(d => d.AgentPath).ToHashSet();

            // Check if any of the delegated agents also delegate back to this agent
            foreach (var delegatedPath in delegatedAgentPaths)
            {
                // Extract agent ID from path (last segment)
                var delegatedId = delegatedPath.Split('/').Last();
                var delegatedAgent = delegatingAgents.FirstOrDefault(a => a.Id == delegatedId);

                if (delegatedAgent?.Delegations != null)
                {
                    var backDelegations = delegatedAgent.Delegations.Select(d => d.AgentPath.Split('/').Last()).ToHashSet();
                    if (backDelegations.Contains(agent.Id))
                    {
                        // Cyclic dependency found
                        cyclicAgents.Add(agent.Id);
                        cyclicAgents.Add(delegatedId);
                    }
                }
            }
        }

        return agents.Where(a => cyclicAgents.Contains(a.Id));
    }

    protected abstract Task<ChatClientAgent> CreateOrUpdateAgentAsync(
        AgentConfiguration agentConfig,
        ChatClientAgent? existingAgent,
        IAgentChat chat,
        IReadOnlyDictionary<string, ChatClientAgent> allAgents);

    protected abstract Task<IEnumerable<ChatClientAgent>> GetExistingAgentsAsync();
    protected abstract string GetAgentName(ChatClientAgent agent);

    /// <summary>
    /// Creates a ChatClient instance for the specified agent configuration.
    /// Implementations should configure the chat client with their specific chat completion provider.
    /// </summary>
    /// <param name="agentConfig">The agent configuration for which to create the chat client</param>
    /// <returns>A configured IChatClient instance</returns>
    protected abstract IChatClient CreateChatClient(AgentConfiguration agentConfig);

    /// <summary>
    /// Gets tools for the specified agent configuration including both plugins and delegation functions.
    /// </summary>
    protected virtual IEnumerable<AITool> GetToolsForAgent(
        AgentConfiguration agentConfig,
        IAgentChat chat,
        IReadOnlyDictionary<string, ChatClientAgent> allAgents)
    {
        var nTools = 0;
        var tools = GetStandardTools(chat).Concat(GetAgentTools(agentConfig, chat, allAgents));

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
    /// The tool combines explicit delegations from the agent's configuration with
    /// hierarchy agents for escalation.
    /// </summary>
    protected virtual IEnumerable<AITool> GetAgentTools(
        AgentConfiguration agentConfig,
        IAgentChat chat,
        IReadOnlyDictionary<string, ChatClientAgent> allAgents)
    {
        // Check if this agent should have delegation capability
        var hasDelegations = agentConfig.Delegations is { Count: > 0 };
        var hasHierarchyAgents = HierarchyAgents.Count > 1; // More than just this agent

        if (!hasDelegations && !hasHierarchyAgents && !agentConfig.IsDefault)
        {
            // No delegation capability needed
            yield break;
        }

        // Create unified delegation tool with all available agents
        var delegationTool = ChatPlugin.CreateUnifiedDelegationTool(
            agentConfig,
            HierarchyAgents,
            Logger);

        Logger.LogInformation("Created unified delegation tool for agent {AgentName} with {HierarchyCount} hierarchy agents",
            agentConfig.Id, HierarchyAgents.Count);

        yield return delegationTool;
    }

    public abstract Task DeleteThreadAsync(string threadId);

    public virtual async Task<IAgentChat> ResumeAsync(ChatConversation messages)
    {
        var ret = await CreateAsync();
        await ret.ResumeAsync(messages);
        return ret;
    }

    public async Task<IReadOnlyList<AgentConfiguration>> GetAgentsAsync(string? contextPath = null)
        => await AgentResolver.GetAgentsForContextAsync(contextPath);

    protected string GetAgentInstructions(AgentConfiguration agentConfig)
    {
        var baseInstructions = agentConfig.Instructions ?? string.Empty;

        // Check if this agent has delegation capability
        var hasDelegations = agentConfig.Delegations is { Count: > 0 };
        var hasHierarchyAgents = HierarchyAgents.Count > 1; // More than just this agent

        if (!hasDelegations && !hasHierarchyAgents && !agentConfig.IsDefault)
        {
            return baseInstructions;
        }

        // Build list of available agents
        var agentList = new List<string>();

        // Add explicit delegations first
        if (agentConfig.Delegations != null)
        {
            foreach (var d in agentConfig.Delegations)
            {
                var agentId = d.AgentPath.Split('/').Last();
                agentList.Add($"- {agentId}: {d.Instructions}");
            }
        }

        // Add hierarchy agents for escalation (excluding current agent and already listed)
        var listedIds = agentConfig.Delegations?.Select(d => d.AgentPath.Split('/').Last()).ToHashSet()
            ?? new HashSet<string>();

        foreach (var agent in HierarchyAgents.Where(a => a.Id != agentConfig.Id && !listedIds.Contains(a.Id)))
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
