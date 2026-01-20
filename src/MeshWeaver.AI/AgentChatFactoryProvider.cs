using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

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
    private readonly IMeshQuery? _meshQuery;
    private bool _preferencesInitialized;

    public AgentChatFactoryProvider(IEnumerable<IAgentChatFactory> factories, IMeshQuery? meshQuery = null)
    {
        _meshQuery = meshQuery;

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
        return CreateAsync(modelName, null);
    }

    public Task<IAgentChat> CreateAsync(string modelName, string? contextPath)
    {
        var factory = GetFactoryForModel(modelName)
            ?? throw new ArgumentException($"No factory can serve model: {modelName}");
        return factory.CreateAsync(modelName, contextPath);
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

    public async Task<IReadOnlyList<AgentWithPath>> GetAgentsWithPathsAsync(string? contextPath = null)
    {
        // Get agents with paths from the first factory (all factories share the same agent resolver)
        if (_factories.Count == 0)
            return new List<AgentWithPath>();

        return await _factories[0].GetAgentsWithPathsAsync(contextPath);
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
        var agentsWithPaths = await GetAgentsWithPathsAsync(contextPath);
        var agents = agentsWithPaths.Select(a => a.Configuration).ToList();
        var indentLevels = CalculateIndentLevels(agents);

        // Build display info list
        var displayInfos = agentsWithPaths
            .Select(a => new AgentDisplayInfo
            {
                Name = a.Configuration.Id,
                Path = a.Path,
                Description = a.Configuration.Description ?? string.Empty,
                GroupName = a.Configuration.GroupName,
                DisplayOrder = a.Configuration.DisplayOrder,
                IndentLevel = indentLevels.GetValueOrDefault(a.Configuration.Id, 0),
                Icon = a.Configuration.Icon,
                CustomIconSvg = a.Configuration.CustomIconSvg,
                AgentConfiguration = a.Configuration
            })
            .ToList();

        // Get NodeType for context path (if MeshQuery available)
        string? nodeTypePath = null;
        if (_meshQuery != null && !string.IsNullOrEmpty(contextPath))
        {
            try
            {
                var normalizedPath = contextPath.TrimStart('/');
                await foreach (var node in _meshQuery.QueryAsync<MeshNode>($"path:{normalizedPath} scope:self"))
                {
                    if (!string.IsNullOrEmpty(node.NodeType) && node.NodeType != "Agent" && node.NodeType != "Markdown")
                    {
                        nodeTypePath = node.NodeType;
                        break;
                    }
                }
            }
            catch
            {
                // Ignore query errors - fall back to no NodeType
            }
        }

        // Order by relevance to context (uses shared helper - single implementation)
        return AgentOrderingHelper.OrderByRelevance(displayInfos, contextPath, nodeTypePath);
    }

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
