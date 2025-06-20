namespace MeshWeaver.AI;

/// <summary>
/// Interface for agents that expose other agents for delegation
/// </summary>
public interface IAgentWithDelegations : IAgentDefinition
{
    /// <summary>
    /// Gets the available agents for delegation along with their descriptions
    /// </summary>
    IEnumerable<DelegationAgent> GetDelegationAgents();
}

/// <summary>
/// Represents an agent available for delegation
/// </summary>
/// <param name="AgentName">The name of the agent</param>
/// <param name="Description">The description of what the agent can do</param>
public record DelegationAgent(string AgentName, string Description);
