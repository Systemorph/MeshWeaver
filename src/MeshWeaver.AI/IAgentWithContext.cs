namespace MeshWeaver.AI;

/// <summary>
/// Interface for agents that can determine if they should handle messages based on chat context.
/// </summary>
public interface IAgentWithContext : IAgentDefinition
{
    /// <summary>
    /// Determines whether this agent should handle messages for the given context.
    /// </summary>
    /// <param name="context">The current chat context containing address and layout area information.</param>
    /// <returns>True if this agent should handle the context, false otherwise.</returns>
    bool Matches(AgentContext? context);
}
