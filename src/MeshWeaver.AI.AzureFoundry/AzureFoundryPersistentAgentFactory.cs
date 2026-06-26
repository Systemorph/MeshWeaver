using Azure.AI.Agents.Persistent;
using Azure.Identity;
using MeshWeaver.AI.Plugins;
using MeshWeaver.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.AI.AzureFoundry;

/// <summary>
/// Factory for creating persistent AI agents using Azure AI Foundry.
/// Persistent agents maintain server-side conversation history,
/// so only new messages need to be sent per interaction.
/// </summary>
public class AzureFoundryPersistentAgentFactory : IChatClientFactory
{
    private readonly IMessageHub hub;
    private readonly AzureFoundryPersistentConfiguration configuration;
    private readonly ILogger<AzureFoundryPersistentAgentFactory> logger;
    private PersistentAgentsClient? persistentClient;

    /// <summary>
    /// Initializes the factory, capturing the hub and resolving the persistent-agent
    /// configuration from <paramref name="options"/>.
    /// </summary>
    /// <param name="hub">Message hub used to resolve plugin services and build mesh tools for created agents.</param>
    /// <param name="options">Persistent Azure AI Foundry configuration (endpoint, models, order).</param>
    /// <param name="logger">Logger for initialization and agent-creation diagnostics.</param>
    public AzureFoundryPersistentAgentFactory(
        IMessageHub hub,
        IOptions<AzureFoundryPersistentConfiguration> options,
        ILogger<AzureFoundryPersistentAgentFactory> logger)
    {
        this.hub = hub;
        this.logger = logger;
        configuration = options.Value ?? throw new ArgumentNullException(nameof(options));

        logger.LogInformation(
            "[AzureFoundryPersistentAgentFactory] Initialized with Endpoint={Endpoint}, ApiKey={HasApiKey}, Models ({ModelCount}): [{Models}]",
            configuration.Endpoint ?? "(null)",
            !string.IsNullOrEmpty(configuration.ApiKey) ? "set" : "MISSING",
            configuration.Models.Length,
            string.Join(", ", configuration.Models));
    }

    /// <summary>Display name of this factory, surfaced in the model/provider listings.</summary>
    public string Name => "Azure Foundry Persistent";

    /// <summary>The model ids this factory advertises as available for persistent agents.</summary>
    public IReadOnlyList<string> Models => configuration.Models;

    /// <summary>Selection priority among factories; lower values are preferred when several factories support the same model.</summary>
    public int Order => configuration.Order;

    /// <summary>Always <c>true</c>: agents from this factory keep server-side conversation history, so only new messages are sent per turn.</summary>
    public bool IsPersistent => true;

    private PersistentAgentsClient GetOrCreateClient()
    {
        if (persistentClient != null)
            return persistentClient;

        if (string.IsNullOrEmpty(configuration.Endpoint))
            throw new InvalidOperationException("Endpoint is required in AzureFoundryPersistentConfiguration");

        // PersistentAgentsClient requires TokenCredential; use DefaultAzureCredential
        // which supports managed identity, Azure CLI, environment variables, etc.
        persistentClient = new PersistentAgentsClient(configuration.Endpoint, new DefaultAzureCredential());

        return persistentClient;
    }

    /// <summary>
    /// Creates a persistent agent server-side in Azure AI Foundry and wraps it as a
    /// <see cref="ChatClientAgent"/>. Resolves the model, composes the agent
    /// instructions (including delegation hints), and registers the agent's tools.
    /// </summary>
    /// <param name="config">Configuration for the agent being created (id, description, instructions, plugins, delegations).</param>
    /// <param name="chat">The active agent chat, used when building mesh/plugin tools.</param>
    /// <param name="existingAgents">Agents already created in this hierarchy, keyed by id, available for delegation wiring.</param>
    /// <param name="hierarchyAgents">All agents in the current hierarchy, used to build the delegation instruction list.</param>
    /// <param name="modelName">Model id selected in the chat composer; falls back to the first configured model when null or empty.</param>
    /// <returns>A task yielding the created persistent <see cref="ChatClientAgent"/>.</returns>
    public async Task<ChatClientAgent> CreateAgentAsync(
        AgentConfiguration config,
        IAgentChat chat,
        IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
        IReadOnlyList<AgentConfiguration> hierarchyAgents,
        string? modelName = null)
    {
        var client = GetOrCreateClient();

        // Model comes from the chat composer selection.
        var model = !string.IsNullOrEmpty(modelName)
            ? modelName
            : configuration.Models.FirstOrDefault()
              ?? throw new InvalidOperationException("No model configured");

        var instructions = GetAgentInstructions(config, hierarchyAgents);
        var name = config.Id;
        var description = config.Description ?? string.Empty;

        // Gather tool definitions for the persistent agent
        var tools = GetToolDefinitions(config, chat, existingAgents, hierarchyAgents);

        logger.LogInformation(
            "Creating persistent Azure AI agent for {AgentName} using model {ModelName} with {ToolCount} tools",
            name, model, tools.Count);

        // Create the agent server-side and wrap as ChatClientAgent.
        // The entire PersistentAgentsClientExtensions class is obsolete — migration to the
        // new Microsoft.Agents.AI.AzureAI SDK is tracked but requires a factory rewrite.
#pragma warning disable CS0618
        var agent = await client.CreateAIAgentAsync(
            model: model,
            name: name,
            description: description,
            instructions: instructions,
            tools: tools);
#pragma warning restore CS0618

        logger.LogInformation(
            "Successfully created persistent agent {AgentName} with server-side ID {AgentId}",
            name, agent.Id);

        return agent;
    }

    /// <summary>
    /// Persistent agents require server-side creation (Azure API call).
    /// This is an external HTTP call, not Orleans — safe to block here.
    /// </summary>
    public ChatClientAgent CreateAgent(
        AgentConfiguration config, IAgentChat chat,
        IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
        IReadOnlyList<AgentConfiguration> hierarchyAgents,
        string? modelName = null)
    {
        throw new NotSupportedException("Persistent agent factory requires async creation. Use CreateAgentAsync.");
    }

    private List<ToolDefinition> GetToolDefinitions(
        AgentConfiguration agentConfig,
        IAgentChat chat,
        IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
        IReadOnlyList<AgentConfiguration> hierarchyAgents)
    {
        var tools = new List<ToolDefinition>();

        // Resolve tools from agent config plugins (same pattern as ChatClientAgentFactory)
        if (agentConfig.Plugins is { Count: > 0 })
        {
            foreach (var pluginRef in agentConfig.Plugins)
            {
                var pluginTools = ResolvePluginTools(pluginRef, chat);
                if (pluginTools != null)
                {
                    foreach (var tool in pluginTools)
                    {
                        if (tool is AIFunction aiFunc)
                            tools.Add(aiFunc.ToToolDefinition());
                    }
                }
                else
                {
                    logger.LogWarning("Plugin '{PluginName}' not found for agent {AgentName}",
                        pluginRef.Name, agentConfig.Id);
                }
            }
        }
        else
        {
            // Legacy mode: Mesh tools (backward compatibility)
            var meshPlugin = new MeshPlugin(hub, chat);
            var description = agentConfig.Description ?? "";
            var needsWriteTools = description.Contains("create", StringComparison.OrdinalIgnoreCase)
                || description.Contains("update", StringComparison.OrdinalIgnoreCase)
                || description.Contains("delete", StringComparison.OrdinalIgnoreCase);
            foreach (var tool in needsWriteTools ? meshPlugin.CreateAllTools() : meshPlugin.CreateTools())
            {
                if (tool is AIFunction aiFunc)
                    tools.Add(aiFunc.ToToolDefinition());
            }
        }

        return tools;
    }

    /// <summary>
    /// Resolves a plugin reference to AITool instances.
    /// Built-in plugin "Mesh" is resolved directly; custom plugins are resolved from DI.
    /// </summary>
    private IEnumerable<AITool>? ResolvePluginTools(
        AgentPluginReference pluginRef,
        IAgentChat chat)
    {
        var allTools = pluginRef.Name switch
        {
            "Mesh" => (IEnumerable<AITool>)new MeshPlugin(hub, chat).CreateAllTools(),
            _ => hub.ServiceProvider.GetServices<IAgentPlugin>()
                    .FirstOrDefault(p => string.Equals(p.Name, pluginRef.Name, StringComparison.OrdinalIgnoreCase))
                    ?.CreateTools()
        };

        if (allTools == null)
            return null;

        // Filter to specific methods if specified
        if (pluginRef.Methods is { Count: > 0 })
        {
            var methodSet = new HashSet<string>(pluginRef.Methods, StringComparer.OrdinalIgnoreCase);
            allTools = allTools.Where(t => t is AIFunction f && methodSet.Contains(f.Name));
        }

        return allTools;
    }

    private static string GetAgentInstructions(AgentConfiguration agentConfig, IReadOnlyList<AgentConfiguration> hierarchyAgents)
    {
        var baseInstructions = agentConfig.Instructions ?? string.Empty;

        var hasDelegations = agentConfig.Delegations is { Count: > 0 };
        var hasHierarchyAgents = hierarchyAgents.Count > 1;

        if (!hasDelegations && !hasHierarchyAgents && !agentConfig.IsDefault)
            return baseInstructions;

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
            return baseInstructions;

        var agentListStr = string.Join('\n', agentList);

        return baseInstructions + $"""

            **Agent Delegation:**
            You have access to a unified Delegate tool to route requests to specialized agents.

            **Available Agents:**
            {agentListStr}
            """;
    }
}

/// <summary>
/// Extension methods for converting AIFunction to Azure AI ToolDefinition.
/// </summary>
internal static class ToolDefinitionExtensions
{
    internal static FunctionToolDefinition ToToolDefinition(this AIFunction aiFunction)
    {
        var parameters = BinaryData.FromString(
            System.Text.Json.JsonSerializer.Serialize(
                aiFunction.JsonSchema));

        return new FunctionToolDefinition(
            aiFunction.Name,
            aiFunction.Description ?? string.Empty,
            parameters);
    }
}
