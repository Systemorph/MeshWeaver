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
    private readonly IStorageAdapter? _storageAdapter;
    private readonly JsonSerializerOptions _jsonOptions;

    public PersistenceActivityLogStore(
        IPersistenceServiceCore persistence,
        JsonSerializerOptions? jsonOptions = null,
        IStorageAdapter? adapter = null)
    {
        _persistence = persistence;
        _jsonOptions = jsonOptions ?? new JsonSerializerOptions();
        _storageAdapter = adapter;
    }

    /// <summary>
    /// Converts a deserialized object to ActivityLog.
    /// Handles the case where JsonSerializer.Deserialize&lt;object&gt; returns JsonElement
    /// (when using default JsonSerializerOptions without polymorphic type info).
    /// </summary>
    private ActivityLog? TryConvertToLog(object obj)
    {
        if (obj is ActivityLog log) return log;
        if (obj is JsonElement element)
        {
            try { return element.Deserialize<ActivityLog>(_jsonOptions); }
            catch { return null; }
        }
        return null;
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
        var logs = new List<ActivityLog>();
        await foreach (var obj in _persistence.GetPartitionObjectsAsync(
            ActivityLogPartition, hubPath, _jsonOptions))
        {
            var log = TryConvertToLog(obj);
            if (log != null)
                logs.Add(log);
        }

        var query = logs.AsEnumerable();

        if (user != null)
            query = query.Where(l => l.User?.Email == user || l.User?.DisplayName == user);
        if (from.HasValue)
            query = query.Where(l => l.Start >= from.Value);
        if (to.HasValue)
            query = query.Where(l => l.End <= to.Value);

        return query.OrderByDescending(l => l.Start).Take(limit).ToList();
    }

    /// <summary>
    /// Enumerates all hub paths under the activity logs partition and aggregates results.
    /// Requires IStorageAdapter for partition enumeration.
    /// </summary>
    public async Task<IReadOnlyList<ActivityLog>> GetRecentActivityLogsAsync(
        string? user = null,
        DateTime? from = null,
        DateTime? to = null,
        int limit = 20,
        CancellationToken ct = default)
    {
        if (_storageAdapter == null)
            return [];

        // Enumerate all hub paths stored under _activitylogs
        var hubPaths = await _storageAdapter.ListPartitionSubPathsAsync(ActivityLogPartition, ct);

        var allLogs = new List<ActivityLog>();
        foreach (var hubPath in hubPaths)
        {
            var logs = await GetActivityLogsAsync(hubPath, user, from, to, limit, ct);
            allLogs.AddRange(logs);
        }

        return allLogs
            .OrderByDescending(l => l.Start)
            .Take(limit)
            .ToList();
    }
}
