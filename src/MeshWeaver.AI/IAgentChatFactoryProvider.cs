namespace MeshWeaver.AI;

/// <summary>
/// Aggregates multiple IAgentChatFactory instances and provides unified model selection.
/// </summary>
public interface IAgentChatFactoryProvider
{
    /// <summary>
    /// All registered factories
    /// </summary>
    IReadOnlyList<IAgentChatFactory> Factories { get; }

    /// <summary>
    /// Union of all models from all factories (for dropdown selection)
    /// </summary>
    IReadOnlyList<string> AllModels { get; }

    /// <summary>
    /// Find the factory that can serve a specific model
    /// </summary>
    IAgentChatFactory? GetFactoryForModel(string modelName);

    /// <summary>
    /// Create a chat with the specified model (finds factory automatically)
    /// </summary>
    Task<IAgentChat> CreateAsync(string modelName);

    /// <summary>
    /// Create a chat with the default model from the first factory
    /// </summary>
    Task<IAgentChat> CreateAsync();

    /// <summary>
    /// Get all agent definitions (from all factories, deduplicated)
    /// </summary>
    Task<IReadOnlyDictionary<string, IAgentDefinition>> GetAgentsAsync();

    /// <summary>
    /// Gets the preferred model for a specific agent.
    /// Returns the agent's preference if it implements IAgentWithModelPreference,
    /// or the user-overridden preference, or the default model.
    /// </summary>
    string GetPreferredModelForAgent(string agentName);

    /// <summary>
    /// Sets a user-override model preference for a specific agent.
    /// This overrides the agent's default preference.
    /// </summary>
    void SetModelPreferenceForAgent(string agentName, string modelName);

    /// <summary>
    /// Gets current model preferences for all agents.
    /// </summary>
    IReadOnlyDictionary<string, string> AgentModelPreferences { get; }

    /// <summary>
    /// Initialize agent preferences based on IAgentWithModelPreference implementations.
    /// Call this after agents are loaded.
    /// </summary>
    Task InitializeAgentPreferencesAsync();
}
