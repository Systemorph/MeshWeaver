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
    /// <remarks>
    /// Default 0 — the in-memory catch-all must NEVER claim ahead of a durable
    /// backend (Priority 100). Scoped providers (a non-null <c>matches</c>
    /// predicate) may pass a higher value to claim their slice first — e.g. the
    /// compile-watcher Release store at 150.
    /// </remarks>
    public int Priority { get; }

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
    /// <param name="priority">
    /// Claim priority. Defaults to <c>0</c> so the unscoped catch-all only claims
    /// after every durable backend (Priority 100) has declined; scoped providers
    /// may pass a higher value to claim their slice first.
    /// </param>
    public InMemoryPartitionStorageProvider(
        InMemoryStorageAdapter adapter,
        IEnumerable<string>? contexts = null,
        Func<string, bool>? matches = null,
        string name = "InMemory",
        int priority = 0)
    {
        // A scoped provider declines paths outside its predicate (the decorator
        // emits null → the try-then-claim chain moves on); the unscoped
        // catch-all claims everything but only at Priority 0 — i.e. after every
        // durable backend has declined.
        Adapter = matches is null
            ? adapter
            : new PathFilteringStorageAdapter(adapter, matches);
        Priority = priority;
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
