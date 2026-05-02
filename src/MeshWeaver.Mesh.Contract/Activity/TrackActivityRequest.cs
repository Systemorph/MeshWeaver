namespace MeshWeaver.Mesh.Activity;

/// <summary>
/// Request to track a user's activity (navigation, login, etc).
/// Handled by the hub to persist <see cref="UserActivityRecord"/> nodes.
/// </summary>
public record TrackActivityRequest(
    string NodePath,
    string UserId,
    string? NodeName,
    string? NodeType,
    string? Namespace
)
{
    /// <summary>
    /// Kind of activity to record. Defaults to <see cref="ActivityType.Read"/>
    /// for back-compat with the original navigation-tracking shape; the auth
    /// middleware uses <see cref="ActivityType.Login"/> when stamping login
    /// events. The handler folds this into the persisted record so Login
    /// entries can be filtered separately from Read in the activity stream.
    /// </summary>
    public ActivityType ActivityType { get; init; } = ActivityType.Read;
}
