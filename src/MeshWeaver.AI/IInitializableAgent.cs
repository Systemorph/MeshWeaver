namespace MeshWeaver.AI;

/// <summary>
/// Interface for agent definitions that require asynchronous initialization.
/// </summary>
public interface IInitializableAgent : IAgentDefinition
{
    /// <summary>
    /// Initializes the agent definition asynchronously.
    /// This method is called during the agent factory initialization process.
    /// </summary>
    /// <returns>A task representing the asynchronous initialization operation.</returns>
    Task InitializeAsync();
}
