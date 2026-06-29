using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;

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
    private readonly ILogger<PersistenceService>? _logger;

    /// <summary>
    /// Builds the ordered provider chain: fixed-namespace ("specific") providers
    /// iterate before wildcard ones, each band sorted by descending
    /// <c>Priority</c> (registration order breaks ties). Caches the writable
    /// subset and merges every provider's <c>Changes</c> feed into one stream.
    /// </summary>
    /// <param name="providers">All registered partition storage providers.</param>
    /// <param name="logger">Optional logger for write-claim diagnostics; may be <c>null</c>.</param>
    public PersistenceService(
        IEnumerable<IPartitionStorageProvider> providers,
        ILogger<PersistenceService>? logger = null)
    {
        _logger = logger;
        // Specific (fixed-namespace) providers iterate first so a /Doc/...
        // path lands on EmbeddedResource before any wildcard gets asked.
        // Within bands, higher IPartitionStorageProvider.Priority claims first
        // (durable backends = 100, in-memory catch-all = 0); ties preserve
        // registration order (OrderByDescending is stable). Without the
        // priority sort, the in-memory wildcard that AddOrleansMeshServices
        // registers as a baseline claimed every write ahead of a Postgres
        // provider registered later — the atioz 2026-06-11 silent create-loss.
        var all = providers.ToList();
        var specific = all
            .Where(p => p.PartitionDefinition != null
                        && !string.IsNullOrEmpty(p.PartitionDefinition.Namespace))
            .OrderByDescending(p => p.Priority)
            .ToList();
        var wildcard = all
            .Where(p => p.PartitionDefinition == null
                        || string.IsNullOrEmpty(p.PartitionDefinition.Namespace))
            .OrderByDescending(p => p.Priority)
            .ToList();
        _allOrdered = specific.Concat(wildcard).ToList();
        _writable = _allOrdered.Where(p => !p.IsReadOnly).ToList();

        // Surface the union of every provider's Changes feed so consumers
        // that subscribe to IStorageAdapter.Changes see writes from any
        // provider (per-node hub reconciliation in MeshDataSource etc.).
        _changes = Observable.Merge(_allOrdered.Select(p => p.Adapter.Changes));
    }

    private readonly IObservable<DataChangeNotification> _changes;

    /// <inheritdoc />
    public IObservable<DataChangeNotification> Changes => _changes;

    /// <summary>
    /// Try each adapter's Read in order; emit the first non-null result, or
    /// null if no adapter has the path. <see cref="Observable.Concat{TSource}(IObservable{IObservable{TSource}})"/> with
    /// <see cref="Observable.FirstOrDefaultAsync{TSource}(IObservable{TSource})"/> keeps the chain lazy —
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
    /// emits the saved node on accept or <c>null</c> on decline. We walk the
    /// chain sequentially (<see cref="Observable.Concat{TSource}(System.Collections.Generic.IEnumerable{IObservable{TSource}})"/>)
    /// and take the FIRST non-null result; if every adapter returned null,
    /// no one saved → throw "couldn't save". This is the canonical pattern
    /// for "must always know where to save" without a central registry.
    /// </summary>
    public IObservable<MeshNode> Write(MeshNode node, JsonSerializerOptions options)
        => TryWriteFrom(node, options, 0)
            .SelectMany(n => n is not null
                ? Observable.Return(n)
                : Observable.Throw<MeshNode>(new InvalidOperationException(
                    $"Could not save '{node.Path}': no writable storage provider accepted the node.")));

    /// <summary>
    /// Sequential try-then-claim, race-free: provider <c>i + 1</c> is subscribed
    /// ONLY after provider <c>i</c> explicitly declined (emitted null / completed
    /// empty). The previous <c>Concat + Take(1)</c> shape advanced to the next
    /// provider on the claimer's synchronous OnCompleted before Take's
    /// unsubscribe landed — a synchronously-emitting claimer (InMemory) raced a
    /// second provider into a DOUBLE WRITE.
    /// </summary>
    private IObservable<MeshNode?> TryWriteFrom(MeshNode node, JsonSerializerOptions options, int index)
        => index >= _writable.Count
            ? Observable.Return<MeshNode?>(null)
            : Observable.Defer(() => _writable[index].Adapter.Write(node, options))
                .Take(1)
                .DefaultIfEmpty()
                .SelectMany(n =>
                {
                    if (n is null)
                        return TryWriteFrom(node, options, index + 1);
                    // Claim diagnostics: which provider actually persisted the
                    // node. Debug-level — flip MeshWeaver...PersistenceService to
                    // Debug to see where a write lands (essential when a wrong
                    // provider claims a path into a non-durable store).
                    var p = _writable[index];
                    _logger?.LogDebug(
                        "[Persistence] write {Path} claimed by {Provider} (adapter {Adapter})",
                        node.Path, p.GetType().Name, p.Adapter.GetType().Name);
                    return Observable.Return<MeshNode?>(n);
                });

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
    /// <see cref="Observable.Any{TSource}(IObservable{TSource})"/> over the merged stream so the chain
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

    /// <inheritdoc />
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

    /// <inheritdoc />
    public IObservable<object> GetPartitionObjects(
        string nodePath, string? subPath, JsonSerializerOptions options)
        => _allOrdered
            .ToObservable()
            .SelectMany(p => p.Adapter.GetPartitionObjects(nodePath, subPath, options)
                .Catch<object, Exception>(_ => Observable.Empty<object>()));

    /// <inheritdoc />
    public IObservable<Unit> SavePartitionObjects(
        string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options)
        => _writable
            .Select(p => p.Adapter.SavePartitionObjects(nodePath, subPath, objects, options))
            .Concat()
            .Take(1)
            .DefaultIfEmpty(Unit.Default);

    /// <inheritdoc />
    public IObservable<Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
        => _writable
            .Select(p => p.Adapter.DeletePartitionObjects(nodePath, subPath))
            .Merge()
            .Aggregate(Unit.Default, (acc, _) => acc);

    /// <inheritdoc />
    public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
        => _allOrdered
            .Select(p => p.Adapter.GetPartitionMaxTimestamp(nodePath, subPath))
            .Merge()
            .Aggregate(default(DateTimeOffset?), (best, current) =>
                current.HasValue && (!best.HasValue || current.Value > best.Value)
                    ? current
                    : best);
}
