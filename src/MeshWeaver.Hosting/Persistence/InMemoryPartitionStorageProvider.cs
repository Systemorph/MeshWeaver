using System.Collections.Concurrent;
using System.Collections.Immutable;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Wildcard <see cref="IPartitionStorageProvider"/> over a shared
/// <see cref="InMemoryStorageAdapter"/>. Useful for tests and the sample
/// catch-all partition. Table dimension is degenerate.
///
/// <para>Register LAST in the routing table when used as a catch-all — the
/// adapter accepts any path so a first-match-wins router would assign it
/// everything not claimed by an earlier provider.</para>
/// </summary>
public sealed class InMemoryPartitionStorageProvider : IPartitionStorageProvider
{
    private readonly ConcurrentDictionary<string, PartitionDefinition> _partitions =
        new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public string Name => "InMemory";

    /// <inheritdoc/>
    public IStorageAdapter Adapter { get; }

    /// <inheritdoc/>
    public ImmutableHashSet<string> Contexts { get; }

    /// <summary>
    /// Constructs a wildcard provider over <paramref name="adapter"/>.
    /// </summary>
    public InMemoryPartitionStorageProvider(
        InMemoryStorageAdapter adapter,
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
    public bool Matches(string fullPath) => !string.IsNullOrWhiteSpace(GetFirstSegment(fullPath));

    /// <inheritdoc/>
    public PartitionDefinition? ResolveDefinition(string fullPath)
    {
        var firstSegment = GetFirstSegment(fullPath);
        if (firstSegment == null) return null;
        return _partitions.GetOrAdd(firstSegment, ns => new PartitionDefinition
        {
            Namespace = ns,
            DataSource = "InMemory",
            Versioned = false
        });
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
