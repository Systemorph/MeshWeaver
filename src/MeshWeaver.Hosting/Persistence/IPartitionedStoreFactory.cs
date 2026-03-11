using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Factory for creating per-partition persistence stores and query providers.
/// Implementations are backend-specific (FileSystem, Cosmos, PostgreSQL).
/// </summary>
public interface IPartitionedStoreFactory
{
    /// <summary>
    /// Creates or provisions a persistence store for the given first-segment partition.
    /// For FileSystem: creates subfolder. For Cosmos: creates container. For PostgreSQL: creates schema.
    /// This operation is idempotent.
    /// </summary>
    /// <param name="firstSegment">The first path segment identifying the partition (e.g., "ACME")</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>A partitioned store containing the storage adapter and optional query provider</returns>
    Task<PartitionedStore> CreateStoreAsync(string firstSegment, CancellationToken ct = default);

    /// <summary>
    /// Discovers existing partitions from the backing store.
    /// For FileSystem: scans for top-level directories.
    /// For Cosmos: lists containers. For PostgreSQL: lists schemas.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of first-segment partition names</returns>
    Task<IReadOnlyList<string>> DiscoverPartitionsAsync(CancellationToken ct = default);

    /// <summary>
    /// Pre-creates storage for default partitions during initialization.
    /// For PostgreSQL: creates schemas, tables, indexes, satellite tables, and triggers.
    /// For FileSystem: no-op (directories are created on demand).
    /// This is idempotent — safe to call multiple times.
    /// Also called when new Partition nodes are created (e.g., organization creation).
    /// </summary>
    Task InitializeDefaultPartitionsAsync(
        IEnumerable<PartitionDefinition> partitions, CancellationToken ct = default)
        => Task.CompletedTask;
}

/// <summary>
/// A store partition consisting of a storage adapter and an optional query provider.
/// The hosting layer creates internal persistence cores from the storage adapter.
/// </summary>
/// <param name="StorageAdapter">The storage adapter for this partition (reads/writes nodes)</param>
/// <param name="QueryProvider">Optional native query provider (e.g., CosmosMeshQuery, PostgreSqlMeshQuery).
/// When null, the InMemoryMeshQuery wrapping the storage adapter is used.</param>
public record PartitionedStore(
    IStorageAdapter StorageAdapter,
    IMeshQueryProvider? QueryProvider = null,
    IVersionQuery? VersionQuery = null
);
