#nullable enable

namespace MeshWeaver.AI;

/// <summary>
/// Interface for agents that have a preferred model.
/// When multiple models are available, the agent can specify which model it prefers.
/// </summary>
public interface IAgentWithModelPreference : IAgentDefinition
{
    /// <summary>
    /// Gets the preferred model name from the list of available models.
    /// </summary>
    /// <param name="availableModels">List of all available models across all factories</param>
    /// <returns>The preferred model name, or null to use the default</returns>
    string? GetPreferredModel(IReadOnlyList<string> availableModels);
}
