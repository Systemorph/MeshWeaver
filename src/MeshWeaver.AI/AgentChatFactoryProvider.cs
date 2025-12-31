using MeshWeaver.Graph.Configuration;

namespace MeshWeaver.AI;

/// <summary>
/// Aggregates multiple IAgentChatFactory instances and provides unified model selection.
/// </summary>
public class AgentChatFactoryProvider : IAgentChatFactoryProvider
{
    private readonly IReadOnlyList<IAgentChatFactory> _factories;
    private readonly Dictionary<string, IAgentChatFactory> _modelToFactory;
    private readonly IReadOnlyList<string> _allModels;
    private readonly Dictionary<string, string> _agentModelPreferences = new();
    private bool _preferencesInitialized;

    public AgentChatFactoryProvider(IEnumerable<IAgentChatFactory> factories)
    {
        // Sort factories by DisplayOrder (lower = first)
        _factories = factories.OrderBy(f => f.DisplayOrder).ToList();
        _modelToFactory = new Dictionary<string, IAgentChatFactory>();

        // Build model → factory lookup (maintaining factory order)
        foreach (var factory in _factories)
        {
            foreach (var model in factory.Models)
            {
                // First factory wins if same model name appears in multiple factories
                if (!_modelToFactory.ContainsKey(model))
                {
                    _modelToFactory[model] = factory;
                }
            }
        }

        // Models are ordered by factory DisplayOrder
        _allModels = _factories
            .SelectMany(f => f.Models)
            .Distinct()
            .ToList();
    }

    public IReadOnlyList<IAgentChatFactory> Factories => _factories;

    public IReadOnlyList<string> AllModels => _allModels;

    public IReadOnlyDictionary<string, string> AgentModelPreferences => _agentModelPreferences;

    public IAgentChatFactory? GetFactoryForModel(string modelName)
        => _modelToFactory.TryGetValue(modelName, out var factory) ? factory : null;

    public Task<IAgentChat> CreateAsync(string modelName)
    {
        var factory = GetFactoryForModel(modelName)
            ?? throw new ArgumentException($"No factory can serve model: {modelName}");
        return factory.CreateAsync(modelName);
    }

    public Task<IAgentChat> CreateAsync()
    {
        if (_factories.Count == 0)
            throw new InvalidOperationException("No factories are registered.");

        var factory = _factories[0];
        return factory.CreateAsync();
    }

    public async Task<IReadOnlyList<AgentConfiguration>> GetAgentsAsync(string? contextPath = null)
    {
        // Get agents from the first factory (all factories share the same agent resolver)
        if (_factories.Count == 0)
            return new List<AgentConfiguration>();

        return await _factories[0].GetAgentsAsync(contextPath);
    }

    public string GetPreferredModelForAgent(string agentName)
    {
        // Check if user has overridden the preference
        if (_agentModelPreferences.TryGetValue(agentName, out var preferredModel))
            return preferredModel;

        // Return default model
        return _allModels.FirstOrDefault() ?? string.Empty;
    }

    public void SetModelPreferenceForAgent(string agentName, string modelName)
    {
        if (!_modelToFactory.ContainsKey(modelName))
            throw new ArgumentException($"Unknown model: {modelName}");

        _agentModelPreferences[agentName] = modelName;
    }

    public async Task InitializeAgentPreferencesAsync(string? contextPath = null)
    {
        if (_preferencesInitialized)
            return;

        var agents = await GetAgentsAsync(contextPath);
        var defaultModel = _allModels.FirstOrDefault() ?? string.Empty;

        foreach (var agentConfig in agents)
        {
            // Use PreferredModel from configuration if set and valid
            if (!string.IsNullOrEmpty(agentConfig.PreferredModel) &&
                _modelToFactory.ContainsKey(agentConfig.PreferredModel))
            {
                _agentModelPreferences[agentConfig.Id] = agentConfig.PreferredModel;
            }
            else
            {
                // Default model for agents without preference
                _agentModelPreferences[agentConfig.Id] = defaultModel;
            }
        }

        _preferencesInitialized = true;
    }

    public async Task<IReadOnlyList<AgentDisplayInfo>> GetAgentsWithDisplayInfoAsync(string? contextPath = null)
    {
        var agents = await GetAgentsAsync(contextPath);
        var indentLevels = CalculateIndentLevels(agents);

        // Build display info dictionary
        var displayInfos = agents
            .Select(a => new AgentDisplayInfo
            {
                Name = a.Id,
                Description = a.Description ?? string.Empty,
                GroupName = a.GroupName,
                DisplayOrder = a.DisplayOrder,
                IndentLevel = indentLevels.GetValueOrDefault(a.Id, 0),
                IconName = a.IconName,
                CustomIconSvg = a.CustomIconSvg,
                AgentConfiguration = a
            })
            .ToDictionary(a => a.Name);

        // Build tree structure with depth-first ordering (children directly below parent)
        return BuildHierarchicalOrder(agents, displayInfos);
    }

    /// <summary>
    /// Builds a hierarchical ordering where children appear directly below their parent.
    /// Root agents are ordered by DisplayOrder, children appear indented below their parent.
    /// </summary>
    private List<AgentDisplayInfo> BuildHierarchicalOrder(
        IReadOnlyList<AgentConfiguration> agents,
        Dictionary<string, AgentDisplayInfo> displayInfos)
    {
        var result = new List<AgentDisplayInfo>();
        var visited = new HashSet<string>();
        var agentsById = agents.ToDictionary(a => a.Id);

        // Find root agents (not delegated to by any other agent)
        var delegatedTo = new HashSet<string>();
        foreach (var agent in agents.Where(a => a.Delegations is { Count: > 0 }))
        {
            foreach (var delegation in agent.Delegations!)
            {
                // Extract agent ID from path
                var targetId = delegation.AgentPath.Split('/').Last();
                delegatedTo.Add(targetId);
            }
        }

        // Get root agents ordered by DisplayOrder
        var rootAgents = displayInfos.Values
            .Where(a => !delegatedTo.Contains(a.Name))
            .OrderBy(a => a.DisplayOrder)
            .ThenBy(a => GetGroupOrder(a.GroupName))
            .ThenBy(a => a.Name)
            .ToList();

        // Depth-first traversal from each root
        foreach (var root in rootAgents)
        {
            AddAgentWithChildren(root.Name, agentsById, displayInfos, result, visited);
        }

        // Add any remaining agents that weren't reachable (orphans)
        foreach (var agentInfo in displayInfos.Values.OrderBy(a => a.DisplayOrder).ThenBy(a => a.Name))
        {
            if (!visited.Contains(agentInfo.Name))
            {
                result.Add(agentInfo);
                visited.Add(agentInfo.Name);
            }
        }

        return result;
    }

    /// <summary>
    /// Recursively adds an agent and its children (delegations) in depth-first order.
    /// </summary>
    private void AddAgentWithChildren(
        string agentName,
        Dictionary<string, AgentConfiguration> agents,
        Dictionary<string, AgentDisplayInfo> displayInfos,
        List<AgentDisplayInfo> result,
        HashSet<string> visited)
    {
        if (visited.Contains(agentName) || !displayInfos.TryGetValue(agentName, out var agentInfo))
            return;

        visited.Add(agentName);
        result.Add(agentInfo);

        // Add children (delegations) directly after parent
        if (agents.TryGetValue(agentName, out var agent) && agent.Delegations is { Count: > 0 })
        {
            var children = agent.Delegations
                .Select(d => d.AgentPath.Split('/').Last())
                .Where(id => displayInfos.ContainsKey(id))
                .Select(id => displayInfos[id])
                .OrderBy(c => c.DisplayOrder)
                .ThenBy(c => c.Name)
                .ToList();

            foreach (var child in children)
            {
                AddAgentWithChildren(child.Name, agents, displayInfos, result, visited);
            }
        }
    }

    /// <summary>
    /// Gets the display order for a group. Documentation is last.
    /// </summary>
    private static int GetGroupOrder(string? groupName) => groupName switch
    {
        "Insurance" => 0,
        "Northwind" => 10,
        "Todo" => 20,
        "Documentation" => 100,
        _ => 50  // Unknown groups in the middle
    };

    /// <summary>
    /// Calculates indent levels based on delegation hierarchy.
    /// Agents that are NOT delegated to by any other agent = indent 0 (root agents).
    /// Agents that ARE delegated to = indent based on depth.
    /// </summary>
    private Dictionary<string, int> CalculateIndentLevels(IReadOnlyList<AgentConfiguration> agents)
    {
        var indentLevels = new Dictionary<string, int>();
        var delegatedTo = new HashSet<string>();
        var agentsById = agents.ToDictionary(a => a.Id);

        // Find all agents that are delegated to
        foreach (var agent in agents.Where(a => a.Delegations is { Count: > 0 }))
        {
            foreach (var delegation in agent.Delegations!)
            {
                var targetId = delegation.AgentPath.Split('/').Last();
                delegatedTo.Add(targetId);
            }
        }

        // Root agents (not delegated to) get indent 0
        foreach (var agent in agents)
        {
            if (!delegatedTo.Contains(agent.Id))
            {
                indentLevels[agent.Id] = 0;
            }
        }

        // Calculate indent for delegated agents using BFS from root agents
        var queue = new Queue<(string AgentName, int Depth)>();
        foreach (var rootAgent in indentLevels.Keys.ToList())
        {
            queue.Enqueue((rootAgent, 0));
        }

        while (queue.Count > 0)
        {
            var (agentName, depth) = queue.Dequeue();

            if (agentsById.TryGetValue(agentName, out var agent) && agent.Delegations is { Count: > 0 })
            {
                foreach (var delegation in agent.Delegations)
                {
                    var childId = delegation.AgentPath.Split('/').Last();
                    if (!indentLevels.ContainsKey(childId) || indentLevels[childId] > depth + 1)
                    {
                        indentLevels[childId] = depth + 1;
                        queue.Enqueue((childId, depth + 1));
                    }
                }
            }
        }

        // Any remaining agents without indent get 0
        foreach (var agent in agents)
        {
            if (!indentLevels.ContainsKey(agent.Id))
            {
                indentLevels[agent.Id] = 0;
            }
        }

        return indentLevels;
    }
}
