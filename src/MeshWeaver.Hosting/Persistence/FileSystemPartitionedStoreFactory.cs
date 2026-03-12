using System.Text.Json;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Factory for creating per-partition FileSystem persistence stores.
/// All partitions share the same CachingStorageAdapter at the base directory
/// for fast in-memory reads (all files pre-loaded at construction).
/// Each partition gets its own InMemoryPersistenceService (with separate cache).
/// </summary>
internal class FileSystemPartitionedStoreFactory : IPartitionedStoreFactory
{
    private readonly string _baseDirectory;
    private readonly Func<JsonSerializerOptions, JsonSerializerOptions>? _writeOptionsModifier;
    private readonly IDataChangeNotifier? _changeNotifier;
    private readonly IStorageAdapter _sharedAdapter;
    private readonly PartitionFilter? _filter;

    public FileSystemPartitionedStoreFactory(
        string baseDirectory,
        Func<JsonSerializerOptions, JsonSerializerOptions>? writeOptionsModifier,
        IDataChangeNotifier? changeNotifier,
        PartitionFilter? filter = null)
    {
        _baseDirectory = baseDirectory;
        _writeOptionsModifier = writeOptionsModifier;
        _changeNotifier = changeNotifier;
        _sharedAdapter = new CachingStorageAdapter(baseDirectory, writeOptionsModifier);
        _filter = filter;
    }

    public Task<PartitionedStore> CreateStoreAsync(string firstSegment, CancellationToken ct = default)
    {
        // Ensure the partition directory exists (idempotent)
        var partitionDir = Path.Combine(_baseDirectory, firstSegment);
        Directory.CreateDirectory(partitionDir);

        // Return the shared adapter — RoutingPersistenceServiceCore creates the core internally
        return Task.FromResult(new PartitionedStore(_sharedAdapter));
    }

    public Task<IReadOnlyList<string>> DiscoverPartitionsAsync(CancellationToken ct = default)
    {
        var partitions = new List<string>();

        if (!Directory.Exists(_baseDirectory))
            return Task.FromResult<IReadOnlyList<string>>(partitions);

        // Scan for top-level directories
        foreach (var dir in Directory.GetDirectories(_baseDirectory))
        {
            var name = Path.GetFileName(dir);
            if (!string.IsNullOrEmpty(name) && !name.StartsWith('.'))
            {
                partitions.Add(name);
            }
        }

        // Also check for top-level .json files (root nodes like "ACME.json")
        foreach (var file in Directory.GetFiles(_baseDirectory, "*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!string.IsNullOrEmpty(name) && !partitions.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                partitions.Add(name);
            }
        }

        // Apply partition filter if configured
        if (_filter != null)
            partitions = partitions.Where(p => _filter.ShouldInclude(p)).ToList();

        return Task.FromResult<IReadOnlyList<string>>(partitions);
    }
}
