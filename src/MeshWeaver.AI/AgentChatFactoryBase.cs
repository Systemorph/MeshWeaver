using MeshWeaver.AI.Persistence;
using MeshWeaver.AI.Plugins;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;

namespace MeshWeaver.AI;

public abstract class AgentChatFactoryBase<TAgent> : IAgentChatFactory
    where TAgent : class
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

        foreach (var agentDefinition in agentDefinitions.Values)
        {
            var existingAgent = agentsByName.GetValueOrDefault(agentDefinition.Name);
            var agent = await CreateOrUpdateAgentAsync(
                agentDefinition,
                existingAgent,
                chatClient);

            // Handle file uploads for agents that implement IAgentDefinitionWithFiles
            if (agentDefinition is IAgentDefinitionWithFiles fileProvider)
                await foreach (var file in fileProvider.GetFilesAsync())
                    await UploadFileAsync(agent, file);

            chatClient.AddAgent(agentDefinition.Name, agent);
        }

        return chatClient;
    }

    protected abstract Task<AIAgent> CreateOrUpdateAgentAsync(IAgentDefinition agentDefinition, TAgent? existingAgent, IAgentChat chat);
    protected abstract Task UploadFileAsync(AIAgent assistant, AgentFileInfo file);


    protected abstract Task<IEnumerable<TAgent>> GetExistingAgentsAsync();
    protected abstract string GetAgentName(TAgent agent);


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
    protected virtual IList<AITool> GetToolsForAgent(IAgentDefinition agentDefinition, IAgentChat chat)
    {
        var tools = new List<AITool>();

        // Get tools from IAgentWithPlugins
        if (agentDefinition is IAgentWithPlugins pluginAgent)
        {
            tools.AddRange(pluginAgent.GetTools(chat));
            Logger.LogDebug("Added {Count} tools for agent {AgentName}", tools.Count, agentDefinition.Name);
        }

        return tools;
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
        var baseInstructions = agentDefinition.Instructions;        // Check if this agent supports delegation
        if (agentDefinition is IAgentWithDelegations delegatingAgent)
        {
            var delegationInstructions = string.Join('\n', delegatingAgent.Delegations.Select(d => $"{d.AgentName}: {d.Instructions}"));

            // Create multiple formats to ensure o3 mini understands exact agent names
            var agentList = string.Join('\n', delegatingAgent.Delegations.Select(d => $"@{d.AgentName}")); var delegationGuidelines =
                $$$"""
                   **Delegation Guidelines:**
                   When users need specialized help, delegate to the appropriate agent based on their needs.
                   
                   **AVAILABLE AGENT NAMES (use these EXACTLY):**
                   {{{agentList}}}
                   
                   **When to delegate:**
                   {{{delegationInstructions}}}
                   
                   **DELEGATION METHOD - USE TOOL:**

                   When you need to delegate to another agent, use the Delegate tool:
                   - agentName: The exact name of the agent to delegate to (use the names from the list above)
                   - message: Your message or task for the agent
                   - askUserFeedback: Set to true if you want to ask for user feedback before proceeding (default: false)

                   **Important:**
                   - The context from the user's message will automatically be included when delegating to other agents. DO NOT INCLUDE THE CONTEXT.
                   - it is not necessary to repeat the message you put into the delegation ==> we will see it after you finish.

                   **Examples:**

                   For immediate delegation:
                   Use the Delegate tool with agentName="ReportingSpecialist" and message="Please create a comprehensive report on the current portfolio performance."

                   For delegation with user feedback:
                   Use the Delegate tool with agentName="DataAnalyst", message="Please analyze the data the user will provide next.", and askUserFeedback=true

                   **Step-by-step delegation process:**
                   1. Find the exact agent name from the "AVAILABLE AGENT NAMES" list above
                   2. Copy it EXACTLY as written (including all letters)
                   3. Use the Delegate tool with the appropriate parameters
                   4. DO NOT INCLUDE THE CONTEXT in your message
                   
                   """;

            // Append delegation guidelines to the base instructions
            return baseInstructions + delegationGuidelines;
        }

        // Check if this agent supports delegations (new interface)
        if (agentDefinition is IAgentWithDelegations delegationsAgent)
        {
            var delegationAgents = delegationsAgent.Delegations.ToList();

            if (delegationAgents.Any())
            {
                var agentList = string.Join('\n', delegationAgents.Select(d => $"@{d.AgentName}"));
                var delegationInstructions = string.Join('\n', delegationAgents.Select(d => $"{d.AgentName}: {d.Instructions}"));

                var delegationGuidelines =
                    $$$"""
                       **Delegation Guidelines:**
                       When users need specialized help, delegate to the appropriate agent based on their needs.
                       
                       **AVAILABLE AGENT NAMES (use these EXACTLY):**
                       {{{agentList}}}
                       
                       **Available Agents:**
                       {{{delegationInstructions}}}
                       
                       **DELEGATION METHOD - USE TOOL:**

                       When you need to delegate to another agent, use the Delegate tool:
                       - agentName: The exact name of the agent to delegate to (use the names from the list above)
                       - message: Your message or task for the agent
                       - askUserFeedback: Set to true if you want to ask for user feedback before proceeding (default: false)
                       
                       **Important:** The context from the user's message will automatically be included when delegating to other agents. DO NOT INCLUDE THE CONTEXT.
                       
                       **Examples:**
                       
                       For immediate delegation:
                       Use the Delegate function with agentName="NorthwindDataAgent" and message="Please analyze the customer data and provide insights."
                       
                       **Step-by-step delegation process:**
                       1. Find the exact agent name from the "AVAILABLE AGENT NAMES" list above
                       2. Copy it EXACTLY as written (including all letters)
                       3. Use the Delegate function with the appropriate parameters
                       4. DO NOT INCLUDE THE CONTEXT in your message
                       
                       """;

                // Append delegation guidelines to the base instructions
                return baseInstructions + delegationGuidelines;
            }
        }

        return baseInstructions;
    }
}
