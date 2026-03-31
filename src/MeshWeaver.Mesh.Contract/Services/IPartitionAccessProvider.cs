namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Provides partition-level access control.
/// Returns the set of partition schemas a user can access, enabling efficient
/// fan-out query filtering without per-partition permission checks.
/// </summary>
public interface IPartitionAccessProvider
{
    /// <summary>
    /// Returns the set of partition schema names the user can access.
    /// Returns null if no filtering should be applied (e.g., admin users).
    /// </summary>
    Task<HashSet<string>?> GetAccessiblePartitionsAsync(string userId, CancellationToken ct = default);
}
