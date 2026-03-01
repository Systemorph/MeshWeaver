using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// IActivityLogStore backed by IPersistenceServiceCore partition storage.
/// Stores activity logs under "_activitylogs" partition key.
/// Works with FileSystem, Cosmos, or any storage adapter.
/// </summary>
public class PersistenceActivityLogStore : IActivityLogStore
{
    private const string ActivityLogPartition = "_activitylogs";
    private readonly IPersistenceServiceCore _persistence;
    private readonly JsonSerializerOptions _jsonOptions;

    public PersistenceActivityLogStore(IPersistenceServiceCore persistence, JsonSerializerOptions? jsonOptions = null)
    {
        _persistence = persistence;
        _jsonOptions = jsonOptions ?? new JsonSerializerOptions();
    }

    public async Task SaveActivityLogAsync(string hubPath, ActivityLog log, CancellationToken ct = default)
    {
        await _persistence.SavePartitionObjectsAsync(
            ActivityLogPartition,
            hubPath,
            [log],
            _jsonOptions,
            ct);
    }

    public async Task<IReadOnlyList<ActivityLog>> GetActivityLogsAsync(
        string hubPath,
        string? user = null,
        DateTime? from = null,
        DateTime? to = null,
        int limit = 50,
        CancellationToken ct = default)
    {
        var objects = new List<object>();
        await foreach (var obj in _persistence.GetPartitionObjectsAsync(
            ActivityLogPartition, hubPath, _jsonOptions))
        {
            objects.Add(obj);
        }

        var query = objects.OfType<ActivityLog>().AsEnumerable();

        if (user != null)
            query = query.Where(l => l.User?.Email == user || l.User?.DisplayName == user);
        if (from.HasValue)
            query = query.Where(l => l.Start >= from.Value);
        if (to.HasValue)
            query = query.Where(l => l.End <= to.Value);

        return query.OrderByDescending(l => l.Start).Take(limit).ToList();
    }

    /// <summary>
    /// Cross-partition query is not supported by partition storage.
    /// Returns empty list. Use PostgreSqlActivityLogStore for cross-hub feed queries.
    /// </summary>
    public Task<IReadOnlyList<ActivityLog>> GetRecentActivityLogsAsync(
        string? user = null,
        DateTime? from = null,
        DateTime? to = null,
        int limit = 20,
        CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<ActivityLog>>([]);
    }
}
