using MeshWeaver.AI.Persistence;
using MeshWeaver.AI.Plugins;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI;

public abstract class AgentChatFactoryBase : IAgentChatFactory
{
    protected readonly IMessageHub Hub;

    /// <summary>
    /// The current model name being used for chat creation
    /// </summary>
    protected string? CurrentModelName { get; private set; }

    protected AgentChatFactoryBase(IMessageHub hub)
    {
        Hub = hub;
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

        // Create AgentChatClient with agent creator delegate
        var chatClient = new AgentChatClient(Hub.ServiceProvider, CreateAgentAsync);

        // Initialize the client with the context path - this loads and creates agents
        await chatClient.InitializeAsync(contextPath);

        return chatClient;
    }

    /// <summary>
    /// Creates a ChatClientAgent for the specified configuration.
    /// This is called by AgentChatClient during initialization.
    /// </summary>
    protected abstract Task<ChatClientAgent> CreateAgentAsync(
        AgentConfiguration agentConfig,
        IAgentChat chat,
        IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
        IReadOnlyList<AgentConfiguration> hierarchyAgents);

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
    /// The tool combines explicit delegations from the agent's configuration with
    /// hierarchy agents for escalation.
    /// </summary>
    protected virtual IEnumerable<AITool> GetAgentTools(
        AgentConfiguration agentConfig,
        IAgentChat chat,
        IReadOnlyDictionary<string, ChatClientAgent> allAgents,
        IReadOnlyList<AgentConfiguration> hierarchyAgents)
    {
        // Check if this agent should have delegation capability
        var hasDelegations = agentConfig.Delegations is { Count: > 0 };
        var hasHierarchyAgents = hierarchyAgents.Count > 1; // More than just this agent

        if (!hasDelegations && !hasHierarchyAgents && !agentConfig.IsDefault)
        {
            // No delegation capability needed
            yield break;
        }

        // Create unified delegation tool with all available agents
        var delegationTool = ChatPlugin.CreateUnifiedDelegationTool(
            agentConfig,
            hierarchyAgents,
            Logger);

        Logger.LogInformation("Created unified delegation tool for agent {AgentName} with {HierarchyCount} hierarchy agents",
            agentConfig.Id, hierarchyAgents.Count);

        yield return delegationTool;
    }

    public abstract Task DeleteThreadAsync(string threadId);

    public virtual async Task<IAgentChat> ResumeAsync(ChatConversation messages)
    {
        var ret = await CreateAsync();
        await ret.ResumeAsync(messages);
        return ret;
    }

    public Task<IReadOnlyList<AgentConfiguration>> GetAgentsAsync(string? contextPath = null)
        => Task.FromResult<IReadOnlyList<AgentConfiguration>>(Array.Empty<AgentConfiguration>());

    public Task<IReadOnlyList<AgentWithPath>> GetAgentsWithPathsAsync(string? contextPath = null)
        => Task.FromResult<IReadOnlyList<AgentWithPath>>(Array.Empty<AgentWithPath>());

    protected string GetAgentInstructions(AgentConfiguration agentConfig, IReadOnlyList<AgentConfiguration> hierarchyAgents)
    {
        var baseInstructions = agentConfig.Instructions ?? string.Empty;

        // Check if this agent has delegation capability
        var hasDelegations = agentConfig.Delegations is { Count: > 0 };
        var hasHierarchyAgents = hierarchyAgents.Count > 1; // More than just this agent

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
