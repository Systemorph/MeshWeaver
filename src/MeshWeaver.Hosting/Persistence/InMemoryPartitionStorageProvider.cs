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

    /// <summary>
    /// Optional caller-supplied path matcher. <c>null</c> = wildcard (matches
    /// any non-empty first segment, the default). Supply a predicate to scope
    /// the adapter to a subset of paths — e.g. compile-watcher Release nodes
    /// at <c>{nodeTypePath}/Release/{version}</c>, registered BEFORE the
    /// wildcard FileSystem provider so first-match-wins routing claims them.
    /// </summary>
    private readonly Func<string, bool>? _matches;

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public IStorageAdapter Adapter { get; }

    /// <inheritdoc/>
    public ImmutableHashSet<string> Contexts { get; }

    /// <summary>
    /// Constructs an in-memory provider over <paramref name="adapter"/>.
    /// </summary>
    /// <param name="adapter">The shared in-memory storage adapter.</param>
    /// <param name="contexts">Optional partition contexts (defaults to the four standard contexts).</param>
    /// <param name="matches">
    /// Optional path predicate; <c>null</c> = wildcard. Supply this to scope
    /// the adapter to specific paths (e.g. Release-segment routing).
    /// </param>
    /// <param name="name">
    /// Diagnostic name. Defaults to <c>"InMemory"</c>; supply a more specific
    /// name (e.g. <c>"InMemory:Release"</c>) so log lines distinguish multiple
    /// in-memory providers.
    /// </param>
    public InMemoryPartitionStorageProvider(
        InMemoryStorageAdapter adapter,
        IEnumerable<string>? contexts = null,
        Func<string, bool>? matches = null,
        string name = "InMemory")
    {
        Adapter = adapter;
        Name = name;
        _matches = matches;
        Contexts = contexts != null
            ? ImmutableHashSet.CreateRange(StringComparer.OrdinalIgnoreCase, contexts)
            : ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
                PartitionContexts.Search,
                PartitionContexts.Create,
                PartitionContexts.Autocomplete,
                PartitionContexts.Browse);
    }

    /// <inheritdoc/>
    public bool Matches(string fullPath)
        => _matches is not null
            ? _matches(fullPath)
            : !string.IsNullOrWhiteSpace(GetFirstSegment(fullPath));

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
