using System.Collections.Immutable;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Wildcard writable <see cref="IPartitionStorageProvider"/> over a shared
/// <see cref="InMemoryStorageAdapter"/>. Useful for tests and the sample
/// catch-all partition. Table dimension is degenerate.
///
/// <para>Routing is implicit: the underlying adapter accepts every Write
/// (or, when the <c>matches</c> predicate is supplied, only paths the
/// predicate accepts; rejected paths return <c>null</c> so the
/// try-then-claim chain falls through to the next writable provider).</para>
/// </summary>
public sealed class InMemoryPartitionStorageProvider : IPartitionStorageProvider
{
    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public bool IsReadOnly => false;

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
    /// Optional path-acceptance predicate. <c>null</c> = wildcard (accept any
    /// non-empty first segment). Supply this to scope the adapter to specific
    /// paths — e.g. compile-watcher Release nodes at
    /// <c>{nodeTypePath}/Release/{version}</c>. When set, the adapter's
    /// <see cref="IStorageAdapter.Write"/> returns <c>null</c> for paths the
    /// predicate rejects so the try-then-claim chain moves on to the next
    /// writable provider.
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
        // TODO(stage2): when IStorageAdapter.Write becomes IObservable<MeshNode?>,
        // wrap `adapter` in PathFilteringStorageAdapter that returns null for paths
        // failing `matches`. For Stage 1 the predicate is ignored — Release-segment
        // routing is temporarily broken until Stage 2 lands.
        _ = matches;
        Adapter = adapter;
        Name = name;
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
