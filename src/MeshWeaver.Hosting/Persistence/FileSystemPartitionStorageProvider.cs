using System.Collections.Immutable;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Wildcard <see cref="IPartitionStorageProvider"/> that exposes a single
/// <see cref="FileSystemStorageAdapter"/> as the catch-all backend for every
/// first segment not claimed by an earlier (more specific) provider.
///
/// <para>Each top-level path segment becomes its own logical partition
/// (synthetic <see cref="PartitionDefinition"/> minted on first sight). The
/// table dimension is degenerate — file storage has no per-table I/O — so
/// <see cref="CreateAdapterForTable"/> returns the same shared adapter for
/// every <c>(def, table)</c> tuple. The per-(schema, table) hub model still
/// applies, just with one hub per partition since <c>table</c> is constant.</para>
///
/// <para>Register LAST in the routing table so earlier specific providers
/// (Embedded, Static, Postgres) get first crack.</para>
/// </summary>
public sealed class FileSystemPartitionStorageProvider : IPartitionStorageProvider
{
    /// <inheritdoc/>
    public string Name => "FileSystem";

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
    public FileSystemPartitionStorageProvider(
        FileSystemStorageAdapter adapter,
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
