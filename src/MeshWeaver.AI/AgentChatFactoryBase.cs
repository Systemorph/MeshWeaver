using MeshWeaver.AI.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
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

        var agentsByName = existingAgents.ToDictionary(GetAgentName);
        var createdAgents = new Dictionary<string, ChatClientAgent>();

        // Order agents: non-delegating agents first, then delegating agents, then default agent last
        var orderedAgents = OrderAgentsForCreation(agentDefinitions.Values);

        // Create a simple context holder that will be used temporarily during agent creation
        // This allows agents to get tools without needing the full orchestration
        var tempContext = new SimpleAgentContext();

        // First pass: Create all agents in order
        foreach (var agentDefinition in orderedAgents)
        {
            var existingAgent = agentsByName.GetValueOrDefault(agentDefinition.Name);
            var agent = await CreateOrUpdateAgentAsync(
                agentDefinition,
                existingAgent,
                tempContext,
                createdAgents); // Pass current dictionary - delegating agents get tools for agents created before them

            // Handle file uploads for agents that implement IAgentDefinitionWithFiles
            if (agentDefinition is IAgentDefinitionWithFiles fileProvider)
                await foreach (var file in fileProvider.GetFilesAsync())
                    await UploadFileAsync(agent, file);

            createdAgents[agentDefinition.Name] = agent;
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
                tempContext,
                createdAgents); // Now all agents exist, so cyclic dependencies can be resolved

            // Replace the agent
            createdAgents[agentDefinition.Name] = updatedAgent;

            Logger.LogInformation("Updated agent {AgentName} with cyclic delegation tools",
                agentDefinition.Name);
        }

        // Build the workflow based on agent delegation relationships
        var workflow = BuildWorkflow(agentDefinitions, createdAgents);

        // Create the AgentChatClient with the workflow
        var chatClient = new AgentChatClient(
            agentDefinitions,
            workflow,
            Hub.ServiceProvider);

        return chatClient;
    }

    /// <summary>
    /// Builds a workflow based on agent delegation relationships.
    /// Uses WorkflowBuilder to connect agents with edges based on IAgentWithDelegations.
    /// </summary>
    private Workflow BuildWorkflow(
        IReadOnlyDictionary<string, IAgentDefinition> agentDefinitions,
        Dictionary<string, ChatClientAgent> createdAgents)
    {
        // Find the default agent (entry point)
        var defaultAgentDef = agentDefinitions.Values
            .FirstOrDefault(a => a.GetType().GetCustomAttributes(typeof(DefaultAgentAttribute), false).Any());

        if (defaultAgentDef == null)
            throw new InvalidOperationException("No default agent found. At least one agent must have [DefaultAgent] attribute.");

        var defaultAgent = createdAgents[defaultAgentDef.Name];

        // Start building workflow with the default agent as entry point
        var workflowBuilder = new WorkflowBuilder(defaultAgent);

        // Build workflow edges based on IAgentWithDelegations
        foreach (var agentDef in agentDefinitions.Values)
        {
            if (agentDef is IAgentWithDelegations delegatingAgent)
            {
                var sourceAgent = createdAgents[agentDef.Name];

                foreach (var delegation in delegatingAgent.Delegations)
                {
                    if (createdAgents.TryGetValue(delegation.AgentName, out var targetAgent))
                    {
                        // Add edge from source agent to target agent
                        workflowBuilder = workflowBuilder.AddEdge(sourceAgent, targetAgent);

                        Logger.LogInformation(
                            "Added workflow edge: {SourceAgent} -> {TargetAgent} ({Instructions})",
                            agentDef.Name,
                            delegation.AgentName,
                            delegation.Instructions);
                    }
                    else
                    {
                        Logger.LogWarning(
                            "Agent {SourceAgent} delegates to {TargetAgent}, but {TargetAgent} was not found",
                            agentDef.Name,
                            delegation.AgentName,
                            delegation.AgentName);
                    }
                }
            }
            // For agents marked with [ExposedInDefaultAgent], add edges from the default agent
            else if (agentDef.GetType().GetCustomAttributes(typeof(ExposedInDefaultAgentAttribute), false).Any()
                     && agentDef.Name != defaultAgentDef.Name)
            {
                var targetAgent = createdAgents[agentDef.Name];
                workflowBuilder = workflowBuilder.AddEdge(defaultAgent, targetAgent);

                Logger.LogInformation(
                    "Added workflow edge from default agent to exposed agent: {DefaultAgent} -> {TargetAgent}",
                    defaultAgentDef.Name,
                    agentDef.Name);
            }
        }

        return workflowBuilder.Build();
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
    /// Gets tools for the specified agent definition including both plugins and context management functions.
    /// </summary>
    protected IEnumerable<AITool> GetToolsForAgent(
        IAgentDefinition agentDefinition,
        IAgentChat chat,
        IReadOnlyDictionary<string, ChatClientAgent> allAgents)
    {
        foreach (var tool in GetContextToolsAsync(agentDefinition, chat, allAgents))
            yield return tool;

        // Get tools from IAgentWithTools
        if (agentDefinition is IAgentWithTools pluginAgent)
        {
            var nTool = 0;
            foreach (var tool in pluginAgent.GetTools(chat))
            {
                ++nTool;
                yield return tool;
            }
            Logger.LogInformation("Agent {AgentName}: Added {Count} plugin tools",
                agentDefinition.Name, nTool);

        }

    }

    /// <summary>
    /// Creates context management tools for all agents.
    /// Returns the ChatPlugin SetContext function.
    /// </summary>
    protected virtual IEnumerable<AITool> GetContextToolsAsync(
        IAgentDefinition agentDefinition,
        IAgentChat chat,
        IReadOnlyDictionary<string, ChatClientAgent> allAgents)
    {
        // All agents get the SetContext tool
        var chatPlugin = new Plugins.ChatPlugin(chat);

        // Create tool directly from the plugin method
        yield return AIFunctionFactory.Create(chatPlugin.SetContext);

        Logger.LogDebug("Added SetContext tool for agent {AgentName}",
            agentDefinition.Name);
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
        // With HandoffOrchestration, delegation is handled natively by the framework
        // So we just return the base instructions without custom delegation prompts
        return agentDefinition.Instructions;
    }

    /// <summary>
    /// Simple context holder used temporarily during agent creation.
    /// This allows agents to get tools without needing the full AgentChatClient.
    /// </summary>
    private class SimpleAgentContext : IAgentChat
    {
        public AgentContext? Context { get; private set; }

        public void SetContext(AgentContext? context)
        {
            Context = context;
        }

        public Task ResumeAsync(ChatConversation conversation)
        {
            throw new NotImplementedException("SimpleAgentContext is only for context storage during agent creation");
        }

        public IAsyncEnumerable<ChatMessage> GetResponseAsync(
            IReadOnlyCollection<ChatMessage> messages,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException("SimpleAgentContext is only for context storage during agent creation");
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IReadOnlyCollection<ChatMessage> messages,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException("SimpleAgentContext is only for context storage during agent creation");
        }

        public void DisplayLayoutArea(LayoutAreaControl layoutAreaControl)
        {
            // No-op during agent creation
        }
    }
}
