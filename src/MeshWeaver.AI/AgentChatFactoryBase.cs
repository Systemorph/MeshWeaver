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
    /// Creates custom delegation functions that signal AgentChatClient to invoke sub-agents in streaming mode.
    /// For IsDefault: adds all agents marked with ExposedInNavigator
    /// For agents with Delegations: adds agents specified in their Delegations property
    /// </summary>
    protected virtual IEnumerable<AITool> GetAgentTools(
        AgentConfiguration agentConfig,
        IAgentChat chat,
        IReadOnlyDictionary<string, ChatClientAgent> allAgents)
    {
        IEnumerable<AgentDelegation> delegations;

        if (agentConfig.IsDefault)
        {
            // Default agent gets all exposed agents (those with ExposedInNavigator = true)
            var allConfigs = AgentResolver.GetAgentsForContextAsync(CurrentContextPath).GetAwaiter().GetResult();
            var exposedAgents = allConfigs
                .Where(a => a.ExposedInNavigator && a.Id != agentConfig.Id);

            delegations = exposedAgents.Select(a => new AgentDelegation
            {
                AgentPath = a.Id,
                Instructions = a.Description
            });
        }
        else if (agentConfig.Delegations is { Count: > 0 })
        {
            // Non-default agent with delegations gets only the agents they specify
            delegations = agentConfig.Delegations;
        }
        else
        {
            // Agent has no delegations
            yield break;
        }

        // Create a delegation tool for each target agent
        foreach (var delegation in delegations)
        {
            // Extract agent ID from path (last segment)
            var targetAgentId = delegation.AgentPath.Split('/').Last();

            // Check if the target agent exists
            if (!allAgents.ContainsKey(targetAgentId))
                continue;

            var tool = ChatPlugin.CreateHandoffTool(
                targetAgentId,
                delegation.Instructions,
                Logger);

            Logger.LogInformation("Created delegation tool - ExpectedName='{ExpectedName}', ActualName='{ActualName}' for agent {AgentName}",
                targetAgentId, tool.Name, agentConfig.Id);

            yield return tool;
        }
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

        // Check if this agent supports delegations
        if (agentConfig.Delegations is { Count: > 0 })
        {
            var agentList = string.Join('\n', agentConfig.Delegations.Select(d =>
            {
                var agentId = d.AgentPath.Split('/').Last();
                return $"- {agentId}: {d.Instructions}";
            }));

            var delegationGuidelines =
                $$$"""

                   **Agent Delegation:**
                   You have access to specialized agents as tools. Each agent appears as a tool with their name.
                   When you need specialized help, simply call the appropriate agent tool with your message.

                   **Available Agents:**
                   {{{agentList}}}

                   **How to delegate:**
                   1. Identify which specialized agent can best handle the user's request
                   2. Call that agent's tool with the message parameter containing what you need them to do
                   3. The agent will handle the request and return their response

                   **Important:**
                   - The context from the user's message is automatically included - don't duplicate it
                   - Each agent is a tool you can call directly by their name
                   - Choose the most appropriate agent based on their specialization

                   """;

            // Append delegation guidelines to the base instructions
            return baseInstructions + delegationGuidelines;
        }
        else if (agentConfig.IsDefault)
        {
            // Default agent gets exposed agents as delegations
            var allConfigs = AgentResolver.GetAgentsForContextAsync(CurrentContextPath).GetAwaiter().GetResult();
            var exposedAgents = allConfigs
                .Where(a => a.ExposedInNavigator && a.Id != agentConfig.Id)
                .ToList();

            if (exposedAgents.Count > 0)
            {
                var agentList = string.Join('\n', exposedAgents.Select(a => $"- {a.Id}: {a.Description}"));

                var delegationGuidelines =
                    $$$"""

                       **Agent Delegation:**
                       You have access to specialized agents as tools. Each agent appears as a tool with their name.
                       When you need specialized help, simply call the appropriate agent tool with your message.

                       **Available Agents:**
                       {{{agentList}}}

                       **How to delegate:**
                       1. Identify which specialized agent can best handle the user's request
                       2. Call that agent's tool with the message parameter containing what you need them to do
                       3. The agent will handle the request and return their response

                       **Important:**
                       - The context from the user's message is automatically included - don't duplicate it
                       - Each agent is a tool you can call directly by their name
                       - Choose the most appropriate agent based on their specialization

                       """;

                return baseInstructions + delegationGuidelines;
            }
        }

        return baseInstructions;
    }
}
