using System.Text.Json;
using MeshWeaver.Mesh.Activity;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// IActivityStore backed by IPersistenceServiceCore partition storage.
/// Stores user activity records under "_useractivity" partition key with userId as sub-path.
/// Works with FileSystem, Cosmos, or any storage adapter.
/// </summary>
public class PersistenceActivityStore : IActivityStore
{
    private const string Partition = "_useractivity";
    private readonly IPersistenceServiceCore _persistence;
    private readonly JsonSerializerOptions _jsonOptions;

    public PersistenceActivityStore(IPersistenceServiceCore persistence, JsonSerializerOptions? jsonOptions = null)
    {
        _persistence = persistence;
        _jsonOptions = jsonOptions ?? new JsonSerializerOptions();
    }

    public async Task<IReadOnlyList<UserActivityRecord>> GetActivitiesAsync(string userId, CancellationToken ct = default)
    {
        var objects = new List<object>();
        await foreach (var obj in _persistence.GetPartitionObjectsAsync(Partition, userId, _jsonOptions))
        {
            objects.Add(obj);
        }

        return objects.OfType<UserActivityRecord>()
            .OrderByDescending(r => r.LastAccessedAt)
            .ToList();
    }

    public async Task SaveActivitiesAsync(string userId, IReadOnlyCollection<UserActivityRecord> records, CancellationToken ct = default)
    {
        // Load existing records to merge
        var existing = new Dictionary<string, UserActivityRecord>(StringComparer.OrdinalIgnoreCase);
        await foreach (var obj in _persistence.GetPartitionObjectsAsync(Partition, userId, _jsonOptions))
        {
            if (obj is UserActivityRecord rec)
                existing[rec.NodePath] = rec;
        }

        // Merge new records with existing
        foreach (var record in records)
        {
            if (existing.TryGetValue(record.NodePath, out var prev))
            {
                existing[record.NodePath] = prev with
                {
                    LastAccessedAt = record.LastAccessedAt,
                    AccessCount = prev.AccessCount + record.AccessCount,
                    ActivityType = record.ActivityType,
                    NodeName = record.NodeName ?? prev.NodeName,
                    NodeType = record.NodeType ?? prev.NodeType,
                    Namespace = record.Namespace ?? prev.Namespace
                };
            }
            else
            {
                existing[record.NodePath] = record;
            }
        }

        // Save all records back
        await _persistence.SavePartitionObjectsAsync(
            Partition,
            userId,
            existing.Values.ToArray(),
            _jsonOptions,
            ct);
    }
}
