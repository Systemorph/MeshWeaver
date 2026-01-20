using MeshWeaver.Graph.Configuration;

namespace MeshWeaver.AI;

/// <summary>
/// Represents an agent configuration with its graph path.
/// </summary>
public record AgentWithPath(AgentConfiguration Configuration, string Path);
