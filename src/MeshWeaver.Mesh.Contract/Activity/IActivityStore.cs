namespace MeshWeaver.Mesh.Activity;

/// <summary>
/// Persists user navigation activity to a dedicated store (not mesh nodes).
/// </summary>
public interface IActivityStore
{
    /// <summary>
    /// Records or updates a single navigation activity (upsert by user+path).
    /// </summary>
    Task TrackActivityAsync(UserActivityRecord record, CancellationToken ct = default);

    /// <summary>
    /// Returns all activities for a user, ordered by last accessed descending.
    /// </summary>
    Task<IReadOnlyList<UserActivityRecord>> GetActivitiesAsync(string userId, CancellationToken ct = default);
}
