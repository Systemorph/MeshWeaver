using System.Text.Json;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Factory for creating per-partition FileSystem persistence stores.
/// All partitions share the same FileSystemStorageAdapter at the base directory.
/// Each partition gets its own FileSystemPersistenceService (with separate cache).
/// </summary>
public class FileSystemPartitionedStoreFactory : IPartitionedStoreFactory
{
    private readonly string _baseDirectory;
    private readonly Func<JsonSerializerOptions, JsonSerializerOptions>? _writeOptionsModifier;
    private readonly IDataChangeNotifier? _changeNotifier;
    private readonly IStorageAdapter _sharedAdapter;

    public FileSystemPartitionedStoreFactory(
        string baseDirectory,
        Func<JsonSerializerOptions, JsonSerializerOptions>? writeOptionsModifier,
        IDataChangeNotifier? changeNotifier)
    {
        _baseDirectory = baseDirectory;
        _writeOptionsModifier = writeOptionsModifier;
        _changeNotifier = changeNotifier;
        _sharedAdapter = new FileSystemStorageAdapter(baseDirectory, writeOptionsModifier);
    }

    public Task<PartitionedStore> CreateStoreAsync(string firstSegment, CancellationToken ct = default)
    {
        // Ensure the partition directory exists (idempotent)
        var partitionDir = Path.Combine(_baseDirectory, firstSegment);
        Directory.CreateDirectory(partitionDir);

        // Create a persistence core that shares the adapter but has its own cache
        var core = new FileSystemPersistenceService(_sharedAdapter, _changeNotifier);

        // Create an InMemoryMeshQuery wrapping the persistence core
        var queryProvider = new InMemoryMeshQuery(core, changeNotifier: _changeNotifier);

        return Task.FromResult(new PartitionedStore(core, queryProvider));
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

        return Task.FromResult<IReadOnlyList<string>>(partitions);
    }
}
