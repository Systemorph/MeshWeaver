using Microsoft.Extensions.AI;

namespace MeshWeaver.AI;

public interface IAgentWithPlugins : IAgentDefinition
{
    /// <summary>
    /// Gets the tools for the agent
    /// </summary>
    /// <param name="chat">The chat for which the tools are instantiated.</param>
    IEnumerable<AITool> GetTools(IAgentChat chat);
}

/// <summary>
/// Used for bedrock, which versions the agents
/// </summary>
public interface IAgentWithVersion : IAgentDefinition
{
    /// <summary>
    /// The version of the agent
    /// </summary>
    public string AgentVersion { get; }
}
