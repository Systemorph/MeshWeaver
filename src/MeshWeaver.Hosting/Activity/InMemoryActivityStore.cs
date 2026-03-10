using System.Collections.Concurrent;
using MeshWeaver.Mesh.Activity;

namespace MeshWeaver.Hosting.Activity;

/// <summary>
/// In-memory implementation of <see cref="IActivityStore"/> for non-PostgreSQL scenarios.
/// Activities are lost on restart.
/// </summary>
public class InMemoryActivityStore : IActivityStore
{
    // Key: (userId, nodePath)
    private readonly ConcurrentDictionary<(string UserId, string NodePath), UserActivityRecord> _records = new();

    public Task TrackActivityAsync(UserActivityRecord record, CancellationToken ct = default)
    {
        _records.AddOrUpdate(
            (record.UserId, record.NodePath),
            record,
            (_, existing) => existing with
            {
                ActivityType = record.ActivityType,
                LastAccessedAt = record.LastAccessedAt,
                AccessCount = existing.AccessCount + 1,
                NodeName = record.NodeName ?? existing.NodeName,
                NodeType = record.NodeType ?? existing.NodeType,
                Namespace = record.Namespace ?? existing.Namespace,
            });

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<UserActivityRecord>> GetActivitiesAsync(string userId, CancellationToken ct = default)
    {
        var results = _records.Values
            .Where(r => r.UserId == userId)
            .OrderByDescending(r => r.LastAccessedAt)
            .ToList();

        return Task.FromResult<IReadOnlyList<UserActivityRecord>>(results);
    }
}
