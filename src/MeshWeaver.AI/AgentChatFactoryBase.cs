using MeshWeaver.AI.Persistence;
using MeshWeaver.AI.Plugins;
using MeshWeaver.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI;

public abstract class AgentChatFactoryBase : IAgentChatFactory
{
    protected readonly IMessageHub Hub;
    protected readonly Task<IReadOnlyDictionary<string, IAgentDefinition>> AgentDefinitions;

    protected AgentChatFactoryBase(
        IMessageHub hub,
        IEnumerable<IAgentDefinition> agentDefinitions)
    {
        this.Hub = hub;
        Logger = hub.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(GetType());
        AgentDefinitions = Initialize(agentDefinitions);
    }
    protected ILogger Logger { get; }

    protected async Task<IReadOnlyDictionary<string, IAgentDefinition>> Initialize(
        IEnumerable<IAgentDefinition> agentDefinitions)
    {
        var dict = new Dictionary<string, IAgentDefinition>();
        foreach (var x in agentDefinitions)
        {
            if (x is IInitializableAgent initializable)
                await initializable.InitializeAsync();
            dict[x.Name] = x;
        }
        return dict;
    }


    public virtual async Task<IAgentChat> CreateAsync()
    {
        var agentDefinitions = await AgentDefinitions;
        var existingAgents = await GetExistingAgentsAsync();

        // Create AgentChatClient first so it can be passed to GetTools()
        var chatClient = new AgentChatClient(agentDefinitions, Hub.ServiceProvider);

        var agentsByName = existingAgents.ToDictionary(GetAgentName);
        var createdAgents = new Dictionary<string, ChatClientAgent>();

        // Order agents: non-delegating agents first, then delegating agents, then default agent last
        var orderedAgents = OrderAgentsForCreation(agentDefinitions.Values);

        // First pass: Create all agents in order without delegation tools
        foreach (var agentDefinition in orderedAgents)
        {
            var existingAgent = agentsByName.GetValueOrDefault(agentDefinition.Name);
            var agent = await CreateOrUpdateAgentAsync(
                agentDefinition,
                existingAgent,
                chatClient,
                createdAgents); // Pass current dictionary - delegating agents get tools for agents created before them

            // Handle file uploads for agents that implement IAgentDefinitionWithFiles
            if (agentDefinition is IAgentDefinitionWithFiles fileProvider)
                await foreach (var file in fileProvider.GetFilesAsync())
                    await UploadFileAsync(agent, file);

            createdAgents[agentDefinition.Name] = agent;
            chatClient.AddAgent(agentDefinition.Name, agent);
        }

        // Second pass: Update agents that have cyclic dependencies
        // Find agents that delegate to each other
        var cyclicAgents = FindCyclicDelegations(agentDefinitions.Values);

        foreach (var agentDefinition in cyclicAgents)
        {
            var existingAgent = agentsByName.GetValueOrDefault(agentDefinition.Name);
            var updatedAgent = await CreateOrUpdateAgentAsync(
                agentDefinition,
                existingAgent,
                chatClient,
                createdAgents); // Now all agents exist, so cyclic dependencies can be resolved

            // Replace the agent in the chat client
            createdAgents[agentDefinition.Name] = updatedAgent;
            chatClient.AddAgent(agentDefinition.Name, updatedAgent);

            Logger.LogInformation("Updated agent {AgentName} with cyclic delegation tools",
                agentDefinition.Name);
        }

        return chatClient;
    }

    /// <summary>
    /// Orders agents for creation: non-delegating first, delegating second, default agent last
    /// </summary>
    private IEnumerable<IAgentDefinition> OrderAgentsForCreation(IEnumerable<IAgentDefinition> agents)
    {
        var agentList = agents.ToList();

        // 1. Non-delegating agents (no IAgentWithDelegations, no [DefaultAgent])
        var nonDelegating = agentList
            .Where(a => a is not IAgentWithDelegations
                     && !a.GetType().GetCustomAttributes(typeof(DefaultAgentAttribute), false).Any());

        // 2. Delegating agents (IAgentWithDelegations but not [DefaultAgent])
        var delegating = agentList
            .Where(a => a is IAgentWithDelegations
                     && !a.GetType().GetCustomAttributes(typeof(DefaultAgentAttribute), false).Any());

        // 3. Default agent (has [DefaultAgent])
        var defaultAgent = agentList
            .Where(a => a.GetType().GetCustomAttributes(typeof(DefaultAgentAttribute), false).Any());

        return nonDelegating.Concat(delegating).Concat(defaultAgent);
    }

    /// <summary>
    /// Finds agents that have cyclic delegations (agents that delegate to each other)
    /// </summary>
    private IEnumerable<IAgentDefinition> FindCyclicDelegations(IEnumerable<IAgentDefinition> agents)
    {
        var delegatingAgents = agents.OfType<IAgentWithDelegations>().ToList();
        var cyclicAgents = new HashSet<string>();

        foreach (var agent in delegatingAgents)
        {
            var delegatedAgentNames = agent.Delegations.Select(d => d.AgentName).ToHashSet();

            // Check if any of the delegated agents also delegate back to this agent
            foreach (var delegatedName in delegatedAgentNames)
            {
                var delegatedAgent = delegatingAgents.FirstOrDefault(a => a.Name == delegatedName);
                if (delegatedAgent != null)
                {
                    var backDelegations = delegatedAgent.Delegations.Select(d => d.AgentName).ToHashSet();
                    if (backDelegations.Contains(agent.Name))
                    {
                        // Cyclic dependency found
                        cyclicAgents.Add(agent.Name);
                        cyclicAgents.Add(delegatedName);
                    }
                }
            }
        }

        return agents.Where(a => cyclicAgents.Contains(a.Name));
    }

    protected abstract Task<ChatClientAgent> CreateOrUpdateAgentAsync(
        IAgentDefinition agentDefinition,
        ChatClientAgent? existingAgent,
        IAgentChat chat,
        IReadOnlyDictionary<string, ChatClientAgent> allAgents);

    protected abstract Task UploadFileAsync(ChatClientAgent assistant, AgentFileInfo file);


    protected abstract Task<IEnumerable<ChatClientAgent>> GetExistingAgentsAsync();
    protected abstract string GetAgentName(ChatClientAgent agent);


    /// <summary>
    /// Creates a ChatClient instance for the specified agent definition.
    /// Implementations should configure the chat client with their specific chat completion provider.
    /// </summary>
    /// <param name="agentDefinition">The agent definition for which to create the chat client</param>
    /// <returns>A configured IChatClient instance</returns>
    protected abstract IChatClient CreateChatClient(IAgentDefinition agentDefinition);

    /// <summary>
    /// Gets tools for the specified agent definition including both plugins and delegation functions.
    /// </summary>
    protected virtual IEnumerable<AITool> GetToolsForAgent(
        IAgentDefinition agentDefinition,
        IAgentChat chat,
        IReadOnlyDictionary<string, ChatClientAgent> allAgents)
    {

        var nTools = 0;
        var tools = GetStandardTools(chat).Concat(GetAgentTools(agentDefinition, chat, allAgents));
        if (agentDefinition is IAgentWithTools agentWithTools)
            tools = tools.Concat(agentWithTools.GetTools(chat));

        foreach (var tool in tools)
        {
            yield return tool;
            nTools++;
        }

        Logger.LogInformation("Agent {AgentName}: Added {Count} plugin tools",
            agentDefinition.Name,
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
    /// For [DefaultAgent]: adds all agents marked with [ExposedInDefaultAgent]
    /// For IAgentWithDelegations: adds agents specified in their Delegations property
    /// </summary>
    protected virtual IEnumerable<AITool> GetAgentTools(
        IAgentDefinition agentDefinition,
        IAgentChat chat,
        IReadOnlyDictionary<string, ChatClientAgent> allAgents)
    {
        // Check if this is the default agent
        var isDefaultAgent = agentDefinition.GetType()
            .GetCustomAttributes(typeof(DefaultAgentAttribute), false)
            .Any();

        IEnumerable<DelegationDescription> delegations;

        if (isDefaultAgent)
        {
            // Default agent gets all exposed agents
            var exposedAgentDefs = AgentDefinitions.Result.Values
                .Where(a => a.GetType().GetCustomAttributes(typeof(ExposedInDefaultAgentAttribute), false).Any())
                .Where(a => a.Name != agentDefinition.Name);

            delegations = exposedAgentDefs.Select(a => new DelegationDescription(a.Name, a.Description));
        }
        else if (agentDefinition is IAgentWithDelegations delegatingAgent)
        {
            // Non-default agent with delegations gets only the agents they specify
            delegations = delegatingAgent.Delegations;
        }
        else
        {
            // Agent has no delegations
            yield break;
        }

        // Create a delegation tool for each target agent
        foreach (var delegation in delegations)
        {
            // Check if the target agent exists
            if (!allAgents.ContainsKey(delegation.AgentName))
                continue;

            yield return ChatPlugin.CreateDelegationTool(
                delegation.AgentName,
                delegation.Instructions,
                Logger);

            Logger.LogDebug("Created delegation tool {ToolName} for agent {AgentName}",
                delegation.AgentName, agentDefinition.Name);
        }
    }

    public abstract Task DeleteThreadAsync(string threadId);
    public virtual async Task<IAgentChat> ResumeAsync(ChatConversation messages)
    {
        var ret = await CreateAsync();
        await ret.ResumeAsync(messages);
        return ret;
    }

    public Task<IReadOnlyDictionary<string, IAgentDefinition>> GetAgentsAsync()
        => AgentDefinitions;

    protected string GetAgentInstructions(IAgentDefinition agentDefinition)
    {
        var baseInstructions = agentDefinition.Instructions;

        // Check if this agent supports delegations
        if (agentDefinition is IAgentWithDelegations delegatingAgent)
        {
            var delegationAgents = delegatingAgent.Delegations.ToList();

            if (delegationAgents.Any())
            {
                var agentList = string.Join('\n', delegationAgents.Select(d => $"- {d.AgentName}: {d.Instructions}"));

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
        }

        return baseInstructions;
    }
}
