using System.Collections.Immutable;

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
    private readonly Lazy<IStorageAdapter> _adapter;

    /// <inheritdoc/>
    public string Name { get; }
    /// <inheritdoc/>
    public bool IsReadOnly => true;
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
        : this(@namespace, provider, description, contexts, ignored: 0)
    {
    }

    /// <summary>
    /// Multi-namespace / pattern rule preserved for source-compat. The lambda
    /// is ignored — read fan-out via <see cref="IMeshQueryProvider"/> handles
    /// path filtering now.
    /// </summary>
    public StaticNodePartitionStorageProvider(
        string name,
        Func<string, bool> matches,
        IStaticNodeProvider provider,
        string? description = null,
        IEnumerable<string>? contexts = null)
        : this(name, provider, description, contexts, ignored: 0)
    {
        _ = matches;
    }

    private StaticNodePartitionStorageProvider(
        string name,
        IStaticNodeProvider provider,
        string? description,
        IEnumerable<string>? contexts,
        int ignored)
    {
        _ = ignored;
        Name = name;
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

}
