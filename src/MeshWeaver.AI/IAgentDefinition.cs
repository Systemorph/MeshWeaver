#nullable enable
using Microsoft.SemanticKernel;

namespace MeshWeaver.AI;

/// <summary>
/// Interface for providing agent definitions
/// </summary>
public interface IAgentDefinition
{
    /// <summary>
    /// Gets the name of the agent
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the description of the agent
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Gets the instructions for the agent
    /// </summary>
    string Instructions { get; }

}
