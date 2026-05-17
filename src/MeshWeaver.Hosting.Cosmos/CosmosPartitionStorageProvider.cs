using System.Collections.Immutable;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Cosmos;

/// <summary>
/// Wildcard <see cref="IPartitionStorageProvider"/> over a shared
/// <see cref="CosmosStorageAdapter"/>. Cosmos does support real per-container
/// I/O isolation, but in the current single-container deployment model the
/// table dimension collapses to degenerate. A future enhancement could
/// produce per-(database, container) adapters when multi-container layouts
/// are wired in.
///
/// <para>Register LAST in the routing table when used as a catch-all.</para>
/// </summary>
public sealed class CosmosPartitionStorageProvider : IPartitionStorageProvider
{
    /// <inheritdoc/>
    public string Name => "Cosmos";

    /// <inheritdoc/>
    public bool IsReadOnly => false;

    /// <inheritdoc/>
    public IStorageAdapter Adapter { get; }

    /// <inheritdoc/>
    public ImmutableHashSet<string> Contexts { get; }

    /// <summary>
    /// Constructs a wildcard provider over <paramref name="adapter"/>.
    /// </summary>
    public CosmosPartitionStorageProvider(
        CosmosStorageAdapter adapter,
        IEnumerable<string>? contexts = null)
    {
        Adapter = adapter;
        Contexts = contexts != null
            ? ImmutableHashSet.CreateRange(StringComparer.OrdinalIgnoreCase, contexts)
            : ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
                PartitionContexts.Search,
                PartitionContexts.Create,
                PartitionContexts.Autocomplete,
                PartitionContexts.Browse);
    }

    /// <inheritdoc/>
    public IStorageAdapter CreateAdapterForTable(PartitionDefinition def, string table) => Adapter;
}
