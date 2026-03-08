using System.Text.Json;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Partitioned store factory that uses CachingStorageAdapter for fast in-memory reads.
/// Pre-loads all files once at construction time.
/// </summary>
internal class CachingPartitionedStoreFactory : IPartitionedStoreFactory
{
    private readonly CachingStorageAdapter _sharedAdapter;
    private readonly PartitionFilter? _filter;

    public CachingPartitionedStoreFactory(
        string baseDirectory,
        Func<JsonSerializerOptions, JsonSerializerOptions>? writeOptionsModifier,
        IDataChangeNotifier? changeNotifier,
        PartitionFilter? filter = null)
    {
        _sharedAdapter = new CachingStorageAdapter(baseDirectory, writeOptionsModifier);
        _filter = filter;
    }

    public Task<PartitionedStore> CreateStoreAsync(string firstSegment, CancellationToken ct = default)
    {
        // Return the shared caching adapter
        return Task.FromResult(new PartitionedStore(_sharedAdapter));
    }

    public Task<IReadOnlyList<string>> DiscoverPartitionsAsync(CancellationToken ct = default)
    {
        var partitions = new List<string>();

        if (!Directory.Exists(_sharedAdapter.BaseDirectory))
            return Task.FromResult<IReadOnlyList<string>>(partitions);

        foreach (var dir in Directory.GetDirectories(_sharedAdapter.BaseDirectory))
        {
            var name = Path.GetFileName(dir);
            if (!string.IsNullOrEmpty(name) && !name.StartsWith('.'))
                partitions.Add(name);
        }

        foreach (var file in Directory.GetFiles(_sharedAdapter.BaseDirectory, "*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!string.IsNullOrEmpty(name) && !partitions.Contains(name, StringComparer.OrdinalIgnoreCase))
                partitions.Add(name);
        }

        if (_filter != null)
            partitions = partitions.Where(p => _filter.ShouldInclude(p)).ToList();

        return Task.FromResult<IReadOnlyList<string>>(partitions);
    }
}
