using System.Collections.Immutable;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.AzureStorage;

/// <summary>
/// Wildcard <see cref="IPartitionStorageProvider"/> over a shared
/// <see cref="AzureBlobStorageAdapter"/>. Table dimension is degenerate —
/// blob paths are the only namespace.
///
/// <para>Register LAST in the routing table when used as a catch-all.</para>
/// </summary>
public sealed class AzureBlobPartitionStorageProvider : IPartitionStorageProvider
{
    /// <inheritdoc/>
    public string Name => "AzureBlob";

    /// <inheritdoc/>
    public bool IsReadOnly => false;

    /// <inheritdoc/>
    /// <remarks>Durable backend - claims ahead of the in-memory wildcard catch-all.</remarks>
    public int Priority => 100;

    /// <inheritdoc/>
    public IStorageAdapter Adapter { get; }

    /// <inheritdoc/>
    public ImmutableHashSet<string> Contexts { get; }

    /// <summary>
    /// Constructs a wildcard provider over <paramref name="adapter"/>.
    /// </summary>
    public AzureBlobPartitionStorageProvider(
        AzureBlobStorageAdapter adapter,
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
