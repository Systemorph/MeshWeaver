namespace MeshWeaver.Mesh.Activity;

/// <summary>
/// Request to track a user's navigation activity.
/// Handled by the hub to persist UserActivityRecord nodes.
/// </summary>
public record TrackActivityRequest(
    string NodePath,
    string UserId,
    string? NodeName,
    string? NodeType,
    string? Namespace
);
