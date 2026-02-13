using System.Collections.Concurrent;
using MeshWeaver.Mesh.Activity;

namespace MeshWeaver.Hosting.Activity;

/// <summary>
/// In-memory implementation of IActivityStore for testing and non-database scenarios.
/// </summary>
public class InMemoryActivityStore : IActivityStore
{
    private readonly ConcurrentDictionary<string, Dictionary<string, UserActivityRecord>> _store = new();

    public Task<IReadOnlyList<UserActivityRecord>> GetActivitiesAsync(string userId, CancellationToken ct = default)
    {
        if (_store.TryGetValue(userId, out var records))
            return Task.FromResult<IReadOnlyList<UserActivityRecord>>(records.Values.OrderByDescending(r => r.LastAccessedAt).ToList());

        return Task.FromResult<IReadOnlyList<UserActivityRecord>>([]);
    }

    public Task SaveActivitiesAsync(string userId, IReadOnlyCollection<UserActivityRecord> records, CancellationToken ct = default)
    {
        var userRecords = _store.GetOrAdd(userId, _ => new Dictionary<string, UserActivityRecord>(StringComparer.OrdinalIgnoreCase));

        foreach (var record in records)
        {
            if (userRecords.TryGetValue(record.NodePath, out var existing))
            {
                userRecords[record.NodePath] = existing with
                {
                    LastAccessedAt = record.LastAccessedAt,
                    AccessCount = existing.AccessCount + record.AccessCount,
                    ActivityType = record.ActivityType,
                    NodeName = record.NodeName ?? existing.NodeName,
                    NodeType = record.NodeType ?? existing.NodeType,
                    Namespace = record.Namespace ?? existing.Namespace
                };
            }
            else
            {
                userRecords[record.NodePath] = record;
            }
        }

        return Task.CompletedTask;
    }
}
