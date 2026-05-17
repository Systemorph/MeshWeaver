using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive.Linq;
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
    private readonly ConcurrentDictionary<string, PartitionDefinition> _partitions =
        new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public string Name => "AzureBlob";

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
    public IObservable<bool> Matches(string fullPath)
        => Observable.Return(!string.IsNullOrWhiteSpace(GetFirstSegment(fullPath)));

    /// <inheritdoc/>
    public IObservable<PartitionDefinition?> ResolveDefinition(string fullPath)
    {
        var firstSegment = GetFirstSegment(fullPath);
        if (firstSegment == null) return Observable.Return<PartitionDefinition?>(null);
        return Observable.Return<PartitionDefinition?>(_partitions.GetOrAdd(firstSegment, ns => new PartitionDefinition
        {
            Namespace = ns,
            DataSource = "AzureBlob",
            Versioned = false
        }));
    }

    /// <inheritdoc/>
    public IStorageAdapter CreateAdapterForTable(PartitionDefinition def, string table) => Adapter;

    private static string? GetFirstSegment(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var normalized = path.Trim('/');
        if (normalized.Length == 0) return null;
        var slash = normalized.IndexOf('/');
        return slash < 0 ? normalized : normalized[..slash];
    }
}
