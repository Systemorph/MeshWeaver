using Microsoft.SemanticKernel;

namespace MeshWeaver.AI;

public interface IAgentWithPlugins : IAgentDefinition
{
    /// <summary>
    /// Gets the plugins for the agent
    /// </summary>
    new IEnumerable<KernelPlugin> GetPlugins();
}

/// <summary>
/// Used for bedrock, which versions the agents
/// </summary>
public interface IAgentWithVersion : IAgentDefinition
{
    /// <summary>
    /// The version of the agent
    /// </summary>
    public string AgentVersion {get;}
}
