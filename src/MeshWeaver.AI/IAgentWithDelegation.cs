namespace MeshWeaver.AI;

/// <summary>
/// Interface for agents that can delegate to other agents
/// </summary>
public interface IAgentWithDelegation : IAgentDefinition
{
    IEnumerable<DelegationDescription> Delegations { get; }
}

/// <summary>
/// Delegates to the agent with the specified name, providing instructions on when to delegate.
/// </summary>
/// <param name="AgentName"></param>
/// <param name="Instructions"></param>
public record DelegationDescription(string AgentName, string Instructions);
