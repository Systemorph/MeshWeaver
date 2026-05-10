using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Factory for creating per-partition in-memory stores. Each partition gets
/// its own <see cref="AdapterPersistenceService"/> wrapping an
/// <see cref="InMemoryStorageAdapter"/> that holds the partition's nodes in
/// a path-keyed dictionary. Static-provider partitions
/// (<see cref="PartitionDefinition"/> with <c>DataSource = "static"</c>)
/// are handled separately by <see cref="RoutingPersistenceServiceCore"/>
/// via <see cref="StaticNodePartitionStore"/> and never reach this factory.
/// </summary>
internal sealed class InMemoryPartitionedStoreFactory : IPartitionedStoreFactory
{
    private readonly ILoggerFactory? _loggerFactory;

    public InMemoryPartitionedStoreFactory(ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory;
    }

    public Task<PartitionedStore> CreateStoreAsync(string firstSegment, CancellationToken ct = default)
        => Task.FromResult(new PartitionedStore(
            new InMemoryStorageAdapter(_loggerFactory?.CreateLogger<InMemoryStorageAdapter>())));

    public Task<IReadOnlyList<string>> DiscoverPartitionsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>([]);

    public Task InitializeDefaultPartitionsAsync(
        IEnumerable<PartitionDefinition> partitions, CancellationToken ct = default)
        => Task.CompletedTask;
}
