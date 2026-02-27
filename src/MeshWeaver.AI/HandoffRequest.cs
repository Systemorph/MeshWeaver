namespace MeshWeaver.AI;

/// <summary>
/// Represents a pending handoff request from one agent to another.
/// The target agent takes over the shared thread and the source agent stops.
/// </summary>
public record HandoffRequest(string SourceAgentName, string TargetAgentName, string Message);
