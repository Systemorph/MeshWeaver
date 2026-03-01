namespace MeshWeaver.Data;

/// <summary>
/// Storage abstraction for persisting bundled ActivityLog entries.
/// </summary>
public interface IActivityLogStore
{
    /// <summary>Save a bundled activity log entry for a given hub path.</summary>
    Task SaveActivityLogAsync(string hubPath, ActivityLog log, CancellationToken ct = default);

    /// <summary>Retrieve activity logs for a hub path, optionally filtered by user and time range.</summary>
    Task<IReadOnlyList<ActivityLog>> GetActivityLogsAsync(
        string hubPath,
        string? user = null,
        DateTime? from = null,
        DateTime? to = null,
        int limit = 50,
        CancellationToken ct = default);
}
