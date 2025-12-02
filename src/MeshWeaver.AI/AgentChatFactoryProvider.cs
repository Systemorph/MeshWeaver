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
        _factories = factories.ToList();
        _modelToFactory = new Dictionary<string, IAgentChatFactory>();

        // Build model → factory lookup
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

        _allModels = _modelToFactory.Keys.ToList();
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
}
