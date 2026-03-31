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

    public string Name => "Azure Foundry Persistent";

    public IReadOnlyList<string> Models => configuration.Models;

    public int Order => configuration.Order;

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

    public async Task<ChatClientAgent> CreateAgentAsync(
        AgentConfiguration config,
        IAgentChat chat,
        IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
        IReadOnlyList<AgentConfiguration> hierarchyAgents,
        string? modelName = null)
    {
        var client = GetOrCreateClient();

        var model = !string.IsNullOrEmpty(modelName)
            ? modelName
            : !string.IsNullOrEmpty(config.PreferredModel)
                ? config.PreferredModel
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

        // Create the agent server-side and wrap as ChatClientAgent
        var agent = await client.CreateAIAgentAsync(
            model: model,
            name: name,
            description: description,
            instructions: instructions,
            tools: tools);

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
