using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Pure-delegation <see cref="IStorageAdapter"/> facade. Implements the
/// no-<c>Matches</c> contract:
///
/// <list type="bullet">
///   <item><b>Read</b> — try each provider's adapter <c>Read</c> in registration
///     order, take the first non-null result. Read-only providers (Embedded,
///     Static) and writable providers participate equally.</item>
///   <item><b>Write</b> — try writable providers only (<see cref="IPartitionStorageProvider.IsReadOnly"/> = false).
///     Each adapter's <see cref="IStorageAdapter.Write"/> returns <c>null</c>
///     when the path isn't theirs; the chain moves on to the next provider.
///     First non-null result wins. Throw if no writable accepts.</item>
///   <item><b>Delete</b> — fan out across <i>every</i> writable provider in
///     parallel; each self-checks containment (read-or-not) and deletes if
///     owned. Multiple owners (rare, but possible during cache races) each
///     delete their copy. The user-facing emit is the deleted path.</item>
///   <item><b>Exists</b> — fan-out OR; any provider reporting true wins.</item>
/// </list>
///
/// <para>Routing is implicit — there's no central "where does this path live?"
/// predicate. Each adapter knows its own scope (via its own cache, partition
/// catalog, dictionary contents) and short-circuits when the path isn't its.</para>
/// </summary>
public sealed class PersistenceService : IStorageAdapter
{
    private readonly IReadOnlyList<IPartitionStorageProvider> _allOrdered;
    private readonly IReadOnlyList<IPartitionStorageProvider> _writable;

    public PersistenceService(IEnumerable<IPartitionStorageProvider> providers)
    {
        // Specific (fixed-namespace) providers iterate first so a /Doc/...
        // path lands on EmbeddedResource before any wildcard gets asked.
        // Within bands, registration order is preserved.
        var all = providers.ToList();
        var specific = all
            .Where(p => p.PartitionDefinition != null
                        && !string.IsNullOrEmpty(p.PartitionDefinition.Namespace))
            .ToList();
        var wildcard = all
            .Where(p => p.PartitionDefinition == null
                        || string.IsNullOrEmpty(p.PartitionDefinition.Namespace))
            .ToList();
        _allOrdered = specific.Concat(wildcard).ToList();
        _writable = _allOrdered.Where(p => !p.IsReadOnly).ToList();
    }

    /// <summary>
    /// Try each adapter's Read in order; emit the first non-null result, or
    /// null if no adapter has the path. <see cref="Observable.Concat"/> with
    /// <see cref="Observable.FirstOrDefaultAsync"/> keeps the chain lazy —
    /// later adapters aren't queried once a hit lands.
    /// </summary>
    public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
        => _allOrdered
            .Select(p => p.Adapter.Read(path, options))
            .Concat()
            .Where(node => node is not null)
            .DefaultIfEmpty(default(MeshNode?))
            .FirstAsync();

    /// <summary>
    /// Try-then-claim: each writable provider's <see cref="IStorageAdapter.Write"/>
    /// emits the saved node on accept or null on decline. Chain stops on first
    /// non-null. Throws when every writable declines (e.g. PG cache says
    /// partition doesn't exist AND no fallback InMemory is registered).
    /// </summary>
    public IObservable<MeshNode> Write(MeshNode node, JsonSerializerOptions options)
        => _writable
            .Select(p => p.Adapter.Write(node, options))
            .Concat()
            .Where(n => n is not null)
            .Select(n => n!)
            .Take(1)
            .Concat(Observable.Throw<MeshNode>(new InvalidOperationException(
                $"No writable storage provider accepted '{node.Path}'.")))
            .Take(1);

    /// <summary>
    /// Fan out across every writable adapter: each self-checks containment
    /// (Read returns non-null) and deletes if owned. Aggregates into the
    /// deleted-path emit. Throws when nothing was actually deleted (every
    /// adapter's Read returned null) — the user's semantics for
    /// "delete-nonexistent must error".
    /// </summary>
    public IObservable<string> Delete(string path)
        => _writable
            .Select(p => p.Adapter.Read(path, JsonSerializerOptionsCache)
                .SelectMany(existing => existing is null
                    ? Observable.Return(false)
                    : p.Adapter.Delete(path).Select(_ => true)))
            .Merge()
            .Aggregate(false, (any, deleted) => any || deleted)
            .SelectMany(any => any
                ? Observable.Return(path)
                : Observable.Throw<string>(new InvalidOperationException(
                    $"Cannot delete '{path}': no writable storage provider has this node.")));

    /// <summary>
    /// Shared JsonSerializerOptions instance for containment-check reads
    /// inside <see cref="Delete"/>. Adapters that don't honour custom
    /// options for a presence check are fine with defaults.
    /// </summary>
    private static readonly JsonSerializerOptions JsonSerializerOptionsCache = new();

    /// <summary>
    /// Fan-out OR: any adapter reporting true wins. Implemented via
    /// <see cref="Observable.Any"/> over the merged stream so the chain
    /// completes as soon as the first true lands.
    /// </summary>
    public IObservable<bool> Exists(string path)
        => _allOrdered
            .Select(p => p.Adapter.Exists(path))
            .Merge()
            .Any(b => b);

    /// <summary>
    /// Deepest prefix across all adapters. Each emits its best prefix; we
    /// pick the one with the largest <c>MatchedSegments</c> (ties broken by
    /// registration order).
    /// </summary>
    public IObservable<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatch(
        string fullPath, JsonSerializerOptions options)
        => _allOrdered
            .Select(p => p.Adapter.FindBestPrefixMatch(fullPath, options))
            .Merge()
            .Aggregate(
                seed: ((MeshNode?)null, MatchedSegments: 0),
                accumulator: (best, current) =>
                    current.MatchedSegments > best.MatchedSegments
                        ? ((MeshNode?)current.Node, current.MatchedSegments)
                        : best);

    /// <summary>
    /// Same fan-out as <see cref="FindBestPrefixMatch"/> but delegates to
    /// each adapter's overridden <see cref="IStorageAdapter.ResolvePath"/>
    /// (Postgres uses a satellite UNION). Deepest match wins.
    /// </summary>
    public IObservable<(MeshNode? Node, int MatchedSegments)> ResolvePath(
        string fullPath, JsonSerializerOptions options)
        => _allOrdered
            .Select(p => p.Adapter.ResolvePath(fullPath, options))
            .Merge()
            .Aggregate(
                seed: ((MeshNode?)null, MatchedSegments: 0),
                accumulator: (best, current) =>
                    current.MatchedSegments > best.MatchedSegments
                        ? ((MeshNode?)current.Node, current.MatchedSegments)
                        : best);

    /// <summary>
    /// Root-level listing fans out to every adapter; non-root listings ask
    /// each adapter (per-adapter scoping returns empty for paths it doesn't
    /// own).
    /// </summary>
    public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)>
        ListChildPaths(string? parentPath)
        => _allOrdered
            .ToObservable()
            .SelectMany(p => p.Adapter.ListChildPaths(parentPath)
                .Catch<(IEnumerable<string>, IEnumerable<string>), Exception>(_ =>
                    Observable.Return<(IEnumerable<string>, IEnumerable<string>)>(([], []))))
            .Aggregate(
                seed: (Nodes: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                       Dirs: new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
                accumulator: (acc, level) =>
                {
                    foreach (var n in level.Item1 ?? Enumerable.Empty<string>()) acc.Nodes.Add(n);
                    foreach (var d in level.Item2 ?? Enumerable.Empty<string>()) acc.Dirs.Add(d);
                    return acc;
                })
            .Select(acc => ((IEnumerable<string>)acc.Nodes, (IEnumerable<string>)acc.Dirs));

    public IObservable<IEnumerable<string>> ListPartitionSubPaths(string nodePath)
        => _allOrdered
            .Select(p => p.Adapter.ListPartitionSubPaths(nodePath))
            .Merge()
            .Aggregate(
                seed: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                accumulator: (acc, paths) =>
                {
                    foreach (var path in paths ?? Enumerable.Empty<string>()) acc.Add(path);
                    return acc;
                })
            .Select(acc => (IEnumerable<string>)acc);

    public IObservable<object> GetPartitionObjects(
        string nodePath, string? subPath, JsonSerializerOptions options)
        => _allOrdered
            .ToObservable()
            .SelectMany(p => p.Adapter.GetPartitionObjects(nodePath, subPath, options)
                .Catch<object, Exception>(_ => Observable.Empty<object>()));

    public IObservable<Unit> SavePartitionObjects(
        string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options)
        => _writable
            .Select(p => p.Adapter.SavePartitionObjects(nodePath, subPath, objects, options))
            .Concat()
            .Take(1)
            .DefaultIfEmpty(Unit.Default);

    public IObservable<Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
        => _writable
            .Select(p => p.Adapter.DeletePartitionObjects(nodePath, subPath))
            .Merge()
            .Aggregate(Unit.Default, (acc, _) => acc);

    public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
        => _allOrdered
            .Select(p => p.Adapter.GetPartitionMaxTimestamp(nodePath, subPath))
            .Merge()
            .Aggregate(default(DateTimeOffset?), (best, current) =>
                current.HasValue && (!best.HasValue || current.Value > best.Value)
                    ? current
                    : best);
}
