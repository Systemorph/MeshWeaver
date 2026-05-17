using System.Collections.Immutable;
using System.Reactive.Linq;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// First-match-wins partition rule that pins one or more namespaces to an
/// <see cref="IStaticNodeProvider"/>'s output. Wraps the provider's nodes
/// in a <see cref="StaticNodeStorageAdapter"/> so the dispatcher can route
/// reads here without a side cache.
///
/// <para>Replaces the legacy <c>SeedIfAbsent</c> fan-in path where every
/// <see cref="IStaticNodeProvider"/> registered globally and the routing
/// core copied each node into the matching writable partition's
/// <c>AdapterPersistenceService</c>. With a partition rule per provider,
/// the static repo IS the partition's storage of record — no in-memory
/// duplication beyond the provider's own node list.</para>
/// </summary>
public sealed class StaticNodePartitionStorageProvider : IPartitionStorageProvider
{
    /// <summary>
    /// Match predicate. Receives the path's first segment (lowercased) for
    /// compatibility with the original first-segment-routing semantics.
    /// </summary>
    private readonly Func<string, bool> _matchesFirstSegment;
    private readonly Lazy<IStorageAdapter> _adapter;

    /// <inheritdoc/>
    public string Name { get; }
    /// <inheritdoc/>
    public IStorageAdapter Adapter => _adapter.Value;
    /// <inheritdoc/>
    public PartitionDefinition? PartitionDefinition { get; }
    /// <inheritdoc/>
    public ImmutableHashSet<string> Contexts { get; }

    /// <summary>
    /// Single-namespace rule. Matches when the path's first segment equals
    /// <paramref name="namespace"/>.
    /// </summary>
    public StaticNodePartitionStorageProvider(
        string @namespace,
        IStaticNodeProvider provider,
        string? description = null,
        IEnumerable<string>? contexts = null)
        : this(@namespace,
              s => string.Equals(s, @namespace, StringComparison.OrdinalIgnoreCase),
              provider,
              description,
              contexts)
    {
    }

    /// <summary>
    /// Lambda-driven rule. <paramref name="matches"/> receives the path's
    /// first segment (lowercased). Use this when a single static provider
    /// owns multiple top-level namespaces or a wildcard pattern.
    /// </summary>
    public StaticNodePartitionStorageProvider(
        string name,
        Func<string, bool> matches,
        IStaticNodeProvider provider,
        string? description = null,
        IEnumerable<string>? contexts = null)
    {
        Name = name;
        _matchesFirstSegment = matches;
        // Lazy: GetStaticNodes() may touch hub-scoped services (e.g. logging) —
        // construct the adapter on first access, after DI is fully wired.
        _adapter = new Lazy<IStorageAdapter>(() =>
            new StaticNodeStorageAdapter(provider.GetStaticNodes()));
        PartitionDefinition = new PartitionDefinition
        {
            Namespace = name,
            DataSource = "static",
            Description = description,
            Versioned = false
        };
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
    {
        var firstSegment = ExtractFirstSegment(fullPath);
        return Observable.Return(firstSegment != null && _matchesFirstSegment(firstSegment));
    }

    private static string? ExtractFirstSegment(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var normalized = path.Trim('/');
        if (normalized.Length == 0) return null;
        var slash = normalized.IndexOf('/');
        return slash < 0 ? normalized : normalized[..slash];
    }
}
