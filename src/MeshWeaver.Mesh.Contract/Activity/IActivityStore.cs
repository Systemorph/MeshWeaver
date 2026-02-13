namespace MeshWeaver.Mesh.Activity;

/// <summary>
/// Storage abstraction for user activity records.
/// </summary>
public interface IActivityStore
{
    /// <summary>
    /// Loads all activity records for a user.
    /// </summary>
    Task<IReadOnlyList<UserActivityRecord>> GetActivitiesAsync(string userId, CancellationToken ct = default);

    /// <summary>
    /// Upserts activity records for a user (merge with existing).
    /// </summary>
    Task SaveActivitiesAsync(string userId, IReadOnlyCollection<UserActivityRecord> records, CancellationToken ct = default);
}
