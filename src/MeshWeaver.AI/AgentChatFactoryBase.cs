using System.Collections.Concurrent;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.Chat;

namespace MeshWeaver.AI;

public abstract class AgentChatFactoryBase<TAgent> : IAgentChatFactory
    where TAgent : class
{
    protected readonly IMessageHub Hub;
    protected readonly IEnumerable<IAgentDefinition> AgentDefinitions;
    protected readonly Task Initialized;
    protected readonly ConcurrentDictionary<string, Agent> Agents = new();

    protected AgentChatFactoryBase(
        IMessageHub hub,
        IEnumerable<IAgentDefinition> agentDefinitions)
    {
        this.Hub = hub;
        this.AgentDefinitions = agentDefinitions;
        Logger = hub.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(GetType());
        Initialized = Initialize();
    }
    protected ILogger Logger { get; }
    protected virtual async Task<Dictionary<string, TAgent>> Initialize()
    {
        var existingAgents = await GetExistingAgentsAsync();

        var agentsByName = existingAgents.ToDictionary(GetAgentName);

        try
        {
            // Initialize all initializable agent definitions first
            foreach (var provider in AgentDefinitions.OfType<IInitializableAgent>())
            {
                await provider.InitializeAsync();
            }

            // Process each agent definition
            foreach (var provider in AgentDefinitions)
            {


                var existingAgent = agentsByName.GetValueOrDefault(provider.AgentName);
                var agent = await CreateOrUpdateAgentAsync(
                    provider,
                    existingAgent);                // Configure plugins for this agent
                foreach (var pluginObject in provider.GetPlugins())
                {
                    var plugin = KernelPluginFactory.CreateFromObject(pluginObject, pluginObject.GetType().Name);
                    agent.Kernel.Plugins.Add(plugin);
                }

                // Handle file uploads for agents that implement IAgentDefinitionWithFiles
                if (provider is IAgentDefinitionWithFiles fileProvider)
                    await foreach (var file in fileProvider.GetFilesAsync())
                        await UploadFileAsync(agent, file);


                Agents[provider.AgentName] = agent;
            }

        }
        catch (Exception ex)
        {
            Logger.LogError("Could not initialize agents: {Exception}", ex);
        }

        return agentsByName;
    }

    protected abstract Task UploadFileAsync(Agent assistant, AgentFileInfo file);


    protected abstract Task<IEnumerable<TAgent>> GetExistingAgentsAsync();
    protected abstract string GetAgentName(TAgent agent);

    protected abstract Task<Agent> CreateOrUpdateAgentAsync(IAgentDefinition agentDefinition, TAgent? existingAgent); protected virtual SelectionStrategy CreateSelectionStrategy(AgentGroupChat chat)
    {
        // Find the agent marked with [DefaultAgent] attribute
        var defaultAgentDefinition = AgentDefinitions
            .FirstOrDefault(a => a.GetType().GetCustomAttributes(typeof(DefaultAgentAttribute), false).Any());

        if (defaultAgentDefinition != null && Agents.TryGetValue(defaultAgentDefinition.AgentName, out var defaultAgent))
        {
            // Return a simple strategy that handles explicit mentions and delegation patterns
            return new DefaultAgentSelectionStrategy(defaultAgent, Logger);
        }

        // Fallback to sequential selection if no default agent is found
        return new SequentialSelectionStrategy();
    }
    public virtual async Task<IAgentChat> CreateAsync()
    {
        await Initialized;

        var ret = new AgentGroupChat(GetAgentArray(Agents)); ret.ExecutionSettings = new()
        {
            SelectionStrategy = CreateSelectionStrategy(ret)
        };
        return new AgentChatClient(ret, Hub.ServiceProvider);
    }




    protected virtual Agent[] GetAgentArray(IReadOnlyDictionary<string, Agent> allAgents)
    {
        return allAgents.Values.ToArray();
    }
    public abstract Task DeleteThreadAsync(string threadId);
    public virtual async Task<IAgentChat> ResumeAsync(ChatConversation messages)
    {
        var ret = await CreateAsync();
        await ret.ResumeAsync(messages);
        return ret;
    }
    protected string GetAgentInstructions(IAgentDefinition agentDefinition)
    {
        var baseInstructions = agentDefinition.Instructions;        // Check if this agent supports delegation
        if (agentDefinition is IAgentWithDelegation delegatingAgent)
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
                     **DELEGATION METHOD - USE CODE BLOCK FORMAT:**
                   
                   When you need to delegate immediately, use this format:
                   ```delegate_to "EXACT_AGENT_NAME"
                   Your message content for the agent goes here.
                   ```
                   
                   
                   **Important:** The context from the user's message will automatically be included when delegating to other agents. DO NOT INCLUDE THE CONTEXT.
                   
                   **Examples:**
                   
                   For immediate delegation:
                   ```delegate_to "ReportingSpecialist"
                   Please create a comprehensive report on the current portfolio performance.
                   ```
                   
                   For delegation with user input:
                   ```delegate_to "DataAnalyst"
                   Please analyze the data the user will provide next.
                   ```
                   
                   **Step-by-step delegation process:**
                   1. Find the exact agent name from the "AVAILABLE AGENT NAMES" list above
                   2. Copy it EXACTLY as written (including all letters)
                   3. Use code block format with delegate_to
                   4. No newline between the backticks, the delegate_to and the agent name. 
                      All must be on one line. 
                   5. Include your message content inside the code block
                   6. DO NOT INCLUDE THE CONTEXT in your message
                   
                   """;

            // Append delegation guidelines to the base instructions
            return baseInstructions + delegationGuidelines;
        }

        // Check if this agent supports delegations (new interface)
        if (agentDefinition is IAgentWithDelegations delegationsAgent)
        {
            var delegationAgents = delegationsAgent.GetDelegationAgents().ToList();

            if (delegationAgents.Any())
            {
                var agentList = string.Join('\n', delegationAgents.Select(d => $"@{d.AgentName}"));
                var delegationInstructions = string.Join('\n', delegationAgents.Select(d => $"{d.AgentName}: {d.Description}"));

                var delegationGuidelines =
                    $$$"""
                       **Delegation Guidelines:**
                       When users need specialized help, delegate to the appropriate agent based on their needs.
                       
                       **AVAILABLE AGENT NAMES (use these EXACTLY):**
                       {{{agentList}}}
                       
                       **Available Agents:**
                       {{{delegationInstructions}}}
                       
                       **DELEGATION METHOD - USE CODE BLOCK FORMAT:**
                       
                       When you need to delegate immediately, use this format:
                       ```delegate_to "EXACT_AGENT_NAME"
                       Your message content for the agent goes here.
                       ```
                       
                       
                       **Important:** The context from the user's message will automatically be included when delegating to other agents. DO NOT INCLUDE THE CONTEXT.
                       
                       **Examples:**
                       
                       For immediate delegation:
                       ```delegate_to "NorthwindDataAgent"
                       Please analyze the customer data and provide insights.
                       ```
                       
                       **Step-by-step delegation process:**
                       1. Find the exact agent name from the "AVAILABLE AGENT NAMES" list above
                       2. Copy it EXACTLY as written (including all letters)
                       3. Use code block format with delegate_to
                       4. No newline between the backticks, the delegate_to and the agent name. 
                          All must be on one line. 
                       5. Include your message content inside the code block
                       6. DO NOT INCLUDE THE CONTEXT in your message
                       
                       """;

                // Append delegation guidelines to the base instructions
                return baseInstructions + delegationGuidelines;
            }
        }

        return baseInstructions;
    }
}
