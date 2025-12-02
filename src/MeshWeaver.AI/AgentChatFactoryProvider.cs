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

    public async Task<IReadOnlyDictionary<string, IAgentDefinition>> GetAgentsAsync()
    {
        // Get agents from the first factory (all factories share the same agent definitions)
        if (_factories.Count == 0)
            return new Dictionary<string, IAgentDefinition>();

        return await _factories[0].GetAgentsAsync();
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

    public async Task InitializeAgentPreferencesAsync()
    {
        if (_preferencesInitialized)
            return;

        var agents = await GetAgentsAsync();
        var defaultModel = _allModels.FirstOrDefault() ?? string.Empty;

        foreach (var (agentName, agentDefinition) in agents)
        {
            if (agentDefinition is IAgentWithModelPreference preferenceAgent)
            {
                var preferredModel = preferenceAgent.GetPreferredModel(_allModels);
                if (!string.IsNullOrEmpty(preferredModel) && _modelToFactory.ContainsKey(preferredModel))
                {
                    _agentModelPreferences[agentName] = preferredModel;
                }
                else
                {
                    _agentModelPreferences[agentName] = defaultModel;
                }
            }
            else
            {
                // Default model for agents without preference
                _agentModelPreferences[agentName] = defaultModel;
            }
        }

        _preferencesInitialized = true;
    }

    public async Task<IReadOnlyList<AgentDisplayInfo>> GetAgentsWithDisplayInfoAsync()
    {
        var agents = await GetAgentsAsync();
        var indentLevels = CalculateIndentLevels(agents);

        // Build display info dictionary
        var displayInfos = agents.Values
            .Select(a => new AgentDisplayInfo
            {
                Name = a.Name,
                Description = a.Description,
                GroupName = a.GroupName,
                DisplayOrder = a.DisplayOrder,
                IndentLevel = indentLevels.GetValueOrDefault(a.Name, 0),
                IconName = a.IconName,
                CustomIconSvg = a.CustomIconSvg,
                AgentDefinition = a
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
        IReadOnlyDictionary<string, IAgentDefinition> agents,
        Dictionary<string, AgentDisplayInfo> displayInfos)
    {
        var result = new List<AgentDisplayInfo>();
        var visited = new HashSet<string>();

        // Find root agents (not delegated to by any other agent)
        var delegatedTo = new HashSet<string>();
        foreach (var agent in agents.Values.OfType<IAgentWithHandoffs>())
        {
            foreach (var delegation in agent.Delegations)
            {
                delegatedTo.Add(delegation.AgentName);
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
            AddAgentWithChildren(root.Name, agents, displayInfos, result, visited);
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
        IReadOnlyDictionary<string, IAgentDefinition> agents,
        Dictionary<string, AgentDisplayInfo> displayInfos,
        List<AgentDisplayInfo> result,
        HashSet<string> visited)
    {
        if (visited.Contains(agentName) || !displayInfos.TryGetValue(agentName, out var agentInfo))
            return;

        visited.Add(agentName);
        result.Add(agentInfo);

        // Add children (delegations) directly after parent
        if (agents.TryGetValue(agentName, out var agent) && agent is IAgentWithHandoffs handoffs)
        {
            var children = handoffs.Delegations
                .Where(d => displayInfos.ContainsKey(d.AgentName))
                .Select(d => displayInfos[d.AgentName])
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
    private Dictionary<string, int> CalculateIndentLevels(IReadOnlyDictionary<string, IAgentDefinition> agents)
    {
        var indentLevels = new Dictionary<string, int>();
        var delegatedTo = new HashSet<string>();

        // Find all agents that are delegated to
        foreach (var agent in agents.Values.OfType<IAgentWithHandoffs>())
        {
            foreach (var delegation in agent.Delegations)
            {
                delegatedTo.Add(delegation.AgentName);
            }
        }

        // Root agents (not delegated to) get indent 0
        foreach (var agentName in agents.Keys)
        {
            if (!delegatedTo.Contains(agentName))
            {
                indentLevels[agentName] = 0;
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

            if (agents.TryGetValue(agentName, out var agent) && agent is IAgentWithHandoffs handoffs)
            {
                foreach (var delegation in handoffs.Delegations)
                {
                    var childName = delegation.AgentName;
                    if (!indentLevels.ContainsKey(childName) || indentLevels[childName] > depth + 1)
                    {
                        indentLevels[childName] = depth + 1;
                        queue.Enqueue((childName, depth + 1));
                    }
                }
            }
        }

        // Any remaining agents without indent get 0
        foreach (var agentName in agents.Keys)
        {
            if (!indentLevels.ContainsKey(agentName))
            {
                indentLevels[agentName] = 0;
            }
        }

        return indentLevels;
    }
}
