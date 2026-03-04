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
    /// Recursively enumerates all hub paths under the activity logs partition
    /// and aggregates results. Hub paths can be nested (e.g., FutuRe/EuropeRe/...),
    /// so we scan recursively through subdirectories.
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

        var allLogs = new List<ActivityLog>();
        await ScanPartitionRecursiveAsync(ActivityLogPartition, null, allLogs, user, from, to, ct);

        return allLogs
            .OrderByDescending(l => l.Start)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// Recursively scans partition directories. At each level:
    /// 1. Try to read ActivityLog objects (json files at this level)
    /// 2. Enumerate subdirectories and recurse into them
    /// </summary>
    private async Task ScanPartitionRecursiveAsync(
        string basePath,
        string? relativePath,
        List<ActivityLog> results,
        string? user,
        DateTime? from,
        DateTime? to,
        CancellationToken ct)
    {
        // Try reading objects at this level
        await foreach (var obj in _persistence.GetPartitionObjectsAsync(
            basePath, relativePath, _jsonOptions))
        {
            var log = TryConvertToLog(obj);
            if (log == null) continue;

            // Set HubPath from the relative path if not already set
            if (log.HubPath == null && relativePath != null)
                log = log with { HubPath = relativePath };

            if (user != null && log.User?.Email != user && log.User?.DisplayName != user)
                continue;
            if (from.HasValue && log.Start < from.Value)
                continue;
            if (to.HasValue && log.End > to.Value)
                continue;

            results.Add(log);
        }

        // Enumerate subdirectories and recurse
        if (_storageAdapter == null) return;
        var fullPath = string.IsNullOrEmpty(relativePath) ? basePath : $"{basePath}/{relativePath}";
        var subPaths = await _storageAdapter.ListPartitionSubPathsAsync(fullPath, ct);
        foreach (var sub in subPaths)
        {
            var newRelative = string.IsNullOrEmpty(relativePath) ? sub : $"{relativePath}/{sub}";
            await ScanPartitionRecursiveAsync(basePath, newRelative, results, user, from, to, ct);
        }
    }
}
