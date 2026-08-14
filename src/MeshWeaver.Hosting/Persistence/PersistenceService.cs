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
///     delete their copy. The user-facing emit is the deleted path.
///     🚨 Read spans MORE providers than Delete can reach, so "readable" and
///     "deletable" are not the same predicate; <see cref="FindDeleteBlockingProvider"/>
///     is how a caller asks the DELETE question before it starts removing
///     things, and Delete classifies rather than blanket-refusing when nothing
///     was removed (#1433).</item>
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
    // The complement of _writable, in read order. A path only THIS set serves is readable but
    // structurally undeletable — see FindDeleteBlockingProvider / Delete (#1433).
    private readonly IReadOnlyList<IPartitionStorageProvider> _readOnly;
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
        // provider registered later — the prod 2026-06-11 silent create-loss.
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
        _readOnly = _allOrdered.Where(p => p.IsReadOnly).ToList();

        // Surface the union of every provider's Changes feed so consumers
        // that subscribe to IStorageAdapter.Changes see writes from any
        // provider (per-node hub reconciliation in MeshDataSource etc.).
        _changes = Observable.Merge(_allOrdered.Select(p => p.Adapter.Changes));

        // Probed once, replayed to every later consult (see LegacyPartitionExists).
        _legacyPartitionExists = Observable
            .Defer(() => _allOrdered.Count == 0
                ? Observable.Return<bool?>(null)
                : Observable
                    .Merge(_allOrdered.Select(p =>
                        // 🚨 Defer + Take(1) + Timeout + Catch — the same shape as
                        // PartitionWriteGuardValidator, and every part of it is load-bearing on a
                        // READ path that a partition-root miss runs through:
                        //   Defer   — a provider that throws SYNCHRONOUSLY when called would throw
                        //             inside this Select, before any observable exists, so the
                        //             Catch below could never see it and the whole probe would
                        //             fault. Deferring turns that into an OnError in the stream.
                        //   Take(1) — the fold is ToList(), which needs every source to COMPLETE.
                        //             A provider that emits and stays open would hang the probe,
                        //             and with it every partition-root read, forever.
                        //   Timeout — a backend that never answers at all is the same wedge. The
                        //             bound is not a tuning knob: the contract already has a word
                        //             for "no answer", and it is `null` = indeterminate. This
                        //             converts a hang into that defined answer instead of silence.
                        Observable.Defer(() => p.PartitionExists(LegacyUserPartitionRepair.LegacyPartition))
                            .Take(1)
                            .Timeout(LegacyProbeTimeout)
                            .Catch((Exception ex) =>
                            {
                                // A provider that cannot answer is INDETERMINATE, never "absent" —
                                // answering false here would disable healing on a genuinely legacy
                                // store because one backend was unreachable.
                                _logger?.LogDebug(ex,
                                    "[Persistence] Legacy-partition probe failed on {Provider}; "
                                    + "treating its answer as indeterminate.", p.GetType().Name);
                                return Observable.Return<bool?>(null);
                            })))
                    .ToList()
                    .Select(answers => answers.Any(a => a is true)
                        ? true
                        : answers.All(a => a is false) ? false : (bool?)null))
            .Replay(1)
            .RefCount();
    }

    private readonly IObservable<DataChangeNotification> _changes;

    /// <inheritdoc />
    public IObservable<DataChangeNotification> Changes => _changes;

    /// <summary>
    /// Try each adapter's Read in order; emit the first non-null result, or
    /// null if no adapter has the path. <see cref="Observable.Concat{TSource}(IObservable{IObservable{TSource}})"/> with
    /// <see cref="Observable.FirstOrDefaultAsync{TSource}(IObservable{TSource})"/> keeps the chain lazy —
    /// later adapters aren't queried once a hit lands. A miss on a bare partition-root path runs
    /// the legacy-user repair (see <see cref="LegacyUserPartitionRepair"/>) before giving up.
    /// </summary>
    public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
        => LegacyUserPartitionRepair.ReadWithRepair(
            path,
            p => ReadCore(p, options),
            n => Write(n, options).Select(saved => (MeshNode?)saved),
            _logger,
            LegacyPartitionExists);

    /// <summary>
    /// Does the legacy <c>User</c> partition exist? OR-folded across providers — one definite
    /// <c>true</c> wins, all-<c>false</c> answers <c>false</c>, and anything indeterminate stays
    /// <see langword="null"/> (the same fold <c>PartitionWriteGuardValidator</c> applies).
    ///
    /// <para>Answered ONCE per service and replayed: the repair consults this on every
    /// partition-root miss, and a schema probe per miss would put a round-trip in front of the
    /// hot read path. Instance field, so the cache dies with the mesh.</para>
    /// </summary>
    private IObservable<bool?> LegacyPartitionExists() => _legacyPartitionExists;

    /// <summary>How long one provider's legacy-partition existence probe may take before it
    /// counts as indeterminate. Same bound as <c>PartitionWriteGuardValidator</c>.</summary>
    private static readonly TimeSpan LegacyProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly IObservable<bool?> _legacyPartitionExists;

    private IObservable<MeshNode?> ReadCore(string path, JsonSerializerOptions options)
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
    /// Sequential try-then-claim for the compare-and-set write, mirroring
    /// <see cref="TryWriteFrom"/>: provider <c>i + 1</c> is asked only after provider <c>i</c>
    /// declined with <c>null</c> ("not my path"). A <c>true</c>/<c>false</c> from a provider is its
    /// VERDICT on the row and ends the walk — a refusal must never be retried against another
    /// store, which would be two chances at a claim that is meant to be exclusive.
    /// Nobody owns the path ⇒ <c>null</c>, which callers read as "no durable arbiter here".
    /// </summary>
    public IObservable<bool?> WriteIfVersion(
        MeshNode node, long expectedVersion, JsonSerializerOptions options)
        => TryWriteIfVersionFrom(node, expectedVersion, options, 0);

    private IObservable<bool?> TryWriteIfVersionFrom(
        MeshNode node, long expectedVersion, JsonSerializerOptions options, int index)
        => index >= _writable.Count
            ? Observable.Return<bool?>(null)
            : Observable.Defer(() => _writable[index].Adapter.WriteIfVersion(node, expectedVersion, options))
                .Take(1)
                .DefaultIfEmpty()
                .SelectMany(applied => applied is null
                    ? TryWriteIfVersionFrom(node, expectedVersion, options, index + 1)
                    : Observable.Return(applied));

    /// <summary>
    /// Fan out across every writable adapter: each self-checks containment
    /// (Read returns non-null) and deletes if owned. Aggregates into the
    /// deleted-path emit.
    ///
    /// <para>🚨 When nothing was deleted, the outcome is CLASSIFIED rather than blanket-refused
    /// (#1433). The old shape threw one <c>InvalidOperationException</c> for two states that mean
    /// opposite things — and, because <see cref="Read"/> consults EVERY provider while this
    /// consults only the writable ones, it threw it for a delete whose requested end state
    /// already held:</para>
    /// <list type="bullet">
    ///   <item><b>Nothing anywhere has the path</b> → it is gone; the delete's requested end state
    ///     HOLDS, so this completes with the path. The caller's existence gate is what answers
    ///     "did this node exist" (<c>HandleDeleteNodeRequest</c> stage 1 →
    ///     <c>NodeNotFound</c>); by the time a commit runs, the row having vanished in between is
    ///     a benign race — a parallel prune, another replica — not a failure. Every concurrent
    ///     delete pruning the same subtree hit this: an ERROR-level "no writable storage provider
    ///     has this node" that reads like a routing bug, for a path that is correctly gone. The
    ///     recursive-delete drain pass already models it exactly this way, with
    ///     <see cref="IStorageAdapter.DeleteIfExists"/>.</item>
    ///   <item><b>A READ-ONLY provider serves it</b> → genuinely undeletable, and now SAID so:
    ///     the refusal names the provider instead of claiming nothing has the node.</item>
    /// </list>
    ///
    /// <para>🚨 What this does NOT do: widen what gets removed. The delete fan-out is still
    /// <see cref="_writable"/> only, still gated on that provider's own containment read — a
    /// read-only provider is never asked to delete, so shipped documentation and static nodes
    /// cannot be tombstoned. The only case that newly SUCCEEDS is the one in which zero bytes are
    /// removed, because there was nothing anywhere to remove.</para>
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
                // No writable provider held it. Ask ONLY the read-only set here: the writable
                // answer is the one we just computed, and re-probing it would let a concurrent
                // RE-CREATE turn this into a false "deleted" for a node that now exists.
                : FindServingReadOnlyProvider(path).SelectMany(blocking => blocking is null
                    ? Observable.Return(path)
                    : Observable.Throw<string>(new InvalidOperationException(
                        $"Cannot delete '{path}': it is served by the READ-ONLY storage provider "
                        + $"'{blocking}', which no delete can remove from. The node is readable but "
                        + "not stored in any writable provider — remove it at its source, or "
                        + "override it in a writable partition first."))));

    /// <inheritdoc />
    /// <remarks>
    /// A WRITABLE holder wins: a path served by BOTH a read-only provider and a writable one (a
    /// db-synced override of a shipped node) is deletable — the delete removes the writable copy —
    /// so only "no writable provider has it, and a read-only one does" blocks.
    /// </remarks>
    public IObservable<string?> FindDeleteBlockingProvider(string path)
        => _readOnly.Count == 0
            // Nothing read-only is registered, so nothing can block. No probe at all — this runs
            // on every user-initiated delete and must cost nothing on the common configuration.
            ? Observable.Return<string?>(null)
            : AnyWritableHas(path).SelectMany(writable => writable
                ? Observable.Return<string?>(null)
                : FindServingReadOnlyProvider(path));

    /// <summary>
    /// The first READ-ONLY provider (in the same order <see cref="Read"/> consults) whose adapter
    /// holds <paramref name="path"/>, or <c>null</c>. A pure read — it never mutates anything.
    /// </summary>
    private IObservable<string?> FindServingReadOnlyProvider(string path)
        => _readOnly.Count == 0
            ? Observable.Return<string?>(null)
            : _readOnly
                .Select(p => p.Adapter.Read(path, JsonSerializerOptionsCache)
                    .Select(node => node is null ? null : DescribeProvider(p)))
                .Concat()
                .Where(name => name is not null)
                .DefaultIfEmpty(null)
                .FirstAsync();

    /// <summary>Fan-out OR: does any WRITABLE provider hold <paramref name="path"/>?</summary>
    private IObservable<bool> AnyWritableHas(string path)
        => _writable.Count == 0
            ? Observable.Return(false)
            : _writable
                .Select(p => p.Adapter.Read(path, JsonSerializerOptionsCache)
                    .Select(node => node is not null))
                .Merge()
                .Aggregate(false, (any, has) => any || has);

    private static string DescribeProvider(IPartitionStorageProvider provider)
        => provider.PartitionDefinition?.DataSource is { Length: > 0 } source
            ? $"{provider.Name} ({source})"
            : provider.Name;

    /// <summary>
    /// Shared JsonSerializerOptions instance for containment-check reads
    /// inside <see cref="Delete"/>. Adapters that don't honour custom
    /// options for a presence check are fine with defaults.
    /// </summary>
    private static readonly JsonSerializerOptions JsonSerializerOptionsCache = new();

    /// <summary>
    /// Fan-out OR across every writable provider's <see cref="IStorageAdapter.DeleteIfExists"/>.
    /// Unlike <see cref="Delete"/> (read-then-delete containment probe), this
    /// delegates the atomicity to each adapter — Postgres answers via the DELETE
    /// row count, in-memory via TryRemove — so concurrent single-use consumers
    /// racing across replicas get exactly one <c>true</c>. Never throws on a
    /// missing node: absent everywhere simply emits <c>false</c>.
    /// </summary>
    public IObservable<bool> DeleteIfExists(string path)
        => _writable
            .Select(p => p.Adapter.DeleteIfExists(path))
            .Merge()
            .Aggregate(false, (any, deleted) => any || deleted);

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

    /// <summary>
    /// Authoritative subtree enumeration for the recursive-delete planner and its
    /// post-delete verification: union of every WRITABLE provider's
    /// <see cref="IStorageAdapter.ListDescendantPaths"/>. Writable-only is deliberate —
    /// the set answers "what must a recursive delete remove / what survived it", and
    /// read-only providers (Embedded, Static) can neither be deleted from nor leak
    /// survivors. Errors propagate: an enumeration that fails must fail the delete
    /// loudly instead of reporting a drained subtree it never actually saw.
    /// </summary>
    public IObservable<IReadOnlyCollection<string>> ListDescendantPaths(string rootPath)
        => _writable
            .Select(p => p.Adapter.ListDescendantPaths(rootPath))
            .Merge()
            .Aggregate(
                seed: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                accumulator: (acc, paths) =>
                {
                    foreach (var path in paths ?? Array.Empty<string>()) acc.Add(path);
                    return acc;
                })
            .Select(acc => (IReadOnlyCollection<string>)acc);

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
