using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Factory for creating per-partition in-memory stores. Each partition gets
/// its own <see cref="InMemoryPersistenceService"/> wrapping a no-op
/// <see cref="InMemoryStorageAdapter"/> — there is no backing store to
/// scan or pre-create. Static-provider partitions
/// (<see cref="PartitionDefinition"/> with <c>DataSource = "static"</c>)
/// are handled separately by <see cref="RoutingPersistenceServiceCore"/>
/// via <see cref="StaticNodePartitionStore"/> and never reach this factory.
/// </summary>
internal sealed class InMemoryPartitionedStoreFactory : IPartitionedStoreFactory
{
    public Task<PartitionedStore> CreateStoreAsync(string firstSegment, CancellationToken ct = default)
        => Task.FromResult(new PartitionedStore(new InMemoryStorageAdapter()));

    public Task<IReadOnlyList<string>> DiscoverPartitionsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>([]);

    public Task InitializeDefaultPartitionsAsync(
        IEnumerable<PartitionDefinition> partitions, CancellationToken ct = default)
        => Task.CompletedTask;
}
