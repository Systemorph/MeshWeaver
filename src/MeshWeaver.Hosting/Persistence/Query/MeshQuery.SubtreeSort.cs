using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting.Persistence.Query;

/// <summary>
/// <c>sort:subtreeLastModified</c> — the engine half of <see cref="SubtreeSort"/>.
///
/// <para>🚨 <b>Why the ENGINE computes it, not the backends.</b> Every clip site orders by a key it
/// can read off the row: each provider's load cap (<c>ORDER BY … LIMIT</c> on Postgres, the
/// pre-cap sort in <see cref="StorageAdapterMeshQueryProvider"/>) and the merge's authoritative
/// re-sort (<see cref="ClipMergedInitial{T}"/>). A subtree aggregate is not on the row, so each of
/// them would key every candidate as null and clip in PATH order — keeping the wrong rows before any
/// later stage could see the right ones, exactly the #4950 shape. So the engine strips the selector
/// and the window, enumerates the candidates, reads one key per candidate through the SAME query
/// path under the SAME identity, orders by <see cref="QuerySortKey"/>'s contract (nulls last, path
/// tiebreak), and only then takes <c>skip</c>/<c>limit</c>. Every backend — in-memory, Postgres,
/// any other — gets the selector with no change of its own, and each applies its own row-level
/// security to the key reads, so a key can only come from a row the caller could have found.</para>
/// </summary>
public partial class MeshQuery
{
    /// <summary>
    /// How many key reads run at once. A concurrency stop on a bounded candidate set
    /// (<see cref="MeshOperationOptions.SubtreeSortMaxCandidates"/>), not a budget: it changes how
    /// many reads are in flight, never how long any may take.
    /// </summary>
    private const int SubtreeKeyConcurrency = 8;

    /// <summary>
    /// The parsed first query when it asks for <see cref="SubtreeSort.SubtreeLastModified"/>, else
    /// null. Guarded by a substring test so the ordinary query path pays no extra parse.
    /// </summary>
    private static ParsedQuery? SubtreeSortOf(MeshQueryRequest request)
    {
        var first = request.EffectiveQueries.FirstOrDefault();
        if (string.IsNullOrEmpty(first)
            || first.IndexOf(SubtreeSort.SubtreeLastModified, StringComparison.OrdinalIgnoreCase) < 0)
            return null;
        var parsed = new QueryParser().Parse(first);
        return SubtreeSort.Applies(parsed.OrderBy) ? parsed : null;
    }

    /// <summary>
    /// The caller's ambient identity, read at SUBSCRIBE time — the same source the providers
    /// resolve a null <see cref="MeshQueryRequest.UserId"/> from.
    /// </summary>
    private string? AmbientUserId()
    {
        try
        {
            var access = hub?.ServiceProvider?.GetService<AccessService>();
            return access?.Context?.ObjectId ?? access?.CircuitContext?.ObjectId;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The secured surface's identity for the key reads, decided ONCE at subscribe time so the
    /// candidates and every key read answer to one viewer. A resolved viewer is stamped on both; an
    /// unresolved one leaves the candidate read to resolve as it always has and stamps the key reads
    /// as the explicit anonymous visitor (<c>""</c>) — never wider than the candidates' viewer,
    /// whatever ambient context a later thread happens to carry.
    /// </summary>
    private (MeshQueryRequest Candidates, MeshQueryRequest Keys) SecuredSubtreeIdentity(MeshQueryRequest request)
    {
        var identity = QueryIdentityResolver.ResolveAndReport(request, AmbientUserId(), Logger);
        return identity.IsUnresolved
            ? (request, request with { UserId = "" })
            : (request with { UserId = identity.UserId }, request with { UserId = identity.UserId });
    }

    /// <summary>
    /// Runs <paramref name="candidatesRequest"/> ordered by the subtree key, reading the keys as
    /// <paramref name="keysRequest"/>. <paramref name="run"/> and <paramref name="runNodes"/> are the
    /// surface the caller entered by (secured or core), so the candidate read and the key reads take
    /// the same route and the same identity.
    ///
    /// <para>🚨 <b>Live changes stay inside the window.</b> The candidate read is UNLIMITED (the
    /// window can only be taken once the keys are known), so its deltas name rows on and off the
    /// page alike. Forwarding them unchanged grew a <c>limit:2</c> result to three on the first
    /// Updated of an off-page container, let an Added bypass the window, and never backfilled a
    /// deleted one. So the ranking keeps the WHOLE candidate set and every key, folds each delta
    /// into it (reading the key of a changed candidate only — one read per changed row), re-takes
    /// the window, and emits the window as a <see cref="QueryChangeType.Reset"/> whenever its
    /// membership, its order, one of its rows or its completeness changed. No row outside the
    /// window is ever emitted, and the order survives.</para>
    ///
    /// <para>🚨 <b>Rows that leave the window are named.</b> The Reset is preceded by a
    /// <see cref="QueryChangeType.Removed"/> for every row that dropped out, so no snapshot in
    /// between ever holds more than the window. The contract says a Reset
    /// replaces the result, but the synced consumer (<c>SyncedQueryMeshNodes</c>) folds a Reset into
    /// its cache additively, so without the eviction a <c>limit:2</c> cache held three rows
    /// (review #4306054491). A consumer that does replace on Reset finds the evicted rows already
    /// gone, so the Removed is a no-op there.</para>
    /// </summary>
    private IObservable<QueryResultChange<T>> OrderBySubtreeActivity<T>(
        MeshQueryRequest candidatesRequest,
        MeshQueryRequest keysRequest,
        ParsedQuery parsed,
        Func<MeshQueryRequest, IObservable<QueryResultChange<T>>> run,
        Func<MeshQueryRequest, IObservable<QueryResultChange<MeshNode>>> runNodes)
    {
        // The candidates: the same query with every sort: and the window removed. NoLimit
        // overrides a `limit:N` token as well; the window is applied after the keys are known.
        var candidates = candidatesRequest with
        {
            Query = SubtreeSort.WithoutSort(candidatesRequest.Query),
            Queries = candidatesRequest.Queries?.Select(SubtreeSort.WithoutSort).ToList(),
            Skip = null,
            Limit = MeshQueryRequest.NoLimit,
        };
        var maxCandidates = OperationOptions.SubtreeSortMaxCandidates;
        // One ranking per SUBSCRIPTION (the outer Defer). 🚨 Apply itself is deferred too: Select
        // runs eagerly, so an undeferred Apply read and mutated the ranking the moment a change
        // ARRIVED — a delta landing while the Initial's key reads were pending saw no window yet and
        // was dropped for good, and a removal could re-rank under a key read still in flight
        // (review #4306054478). Deferred, each Apply starts only when Concat reaches it, after the
        // change before it has finished — so the ranking is touched by one change at a time, in order.
        return Observable.Defer(() =>
        {
            var ranking = new SubtreeRanking<T>(candidatesRequest, keysRequest, parsed, maxCandidates, runNodes);
            return run(candidates)
                .Select(change => Observable.Defer(() => ranking.Apply(change)))
                .Concat();
        });
    }

    /// <summary>
    /// The ranked stream as <see cref="QueryResult"/> snapshots, for the snapshot surface
    /// (<see cref="Query(MeshQueryRequest)"/>). Each snapshot is the current window IN RANK ORDER:
    /// a full frame replaces it, a Removed takes rows out (the evictions that precede a Reset), and
    /// a Removed is emitted as the smaller window so no snapshot exceeds the window either.
    /// </summary>
    private static IObservable<IReadOnlyCollection<QueryResult>> SubtreeSortedSnapshots(
        IObservable<QueryResultChange<MeshNode>> ranked)
        => ranked
            .Scan((IReadOnlyList<QueryResult>)[], (window, change) => change.ChangeType switch
            {
                QueryChangeType.Initial or QueryChangeType.Reset => change.Items
                    .Select((node, i) => QueryResult.FromNode(node,
                        score: change.Scores is { } s && i < s.Count ? s[i] : 0.0))
                    .ToList(),
                QueryChangeType.Removed => window
                    .Where(row => !change.Items.Any(node =>
                        string.Equals(node.Path, row.Path, StringComparison.OrdinalIgnoreCase)))
                    .ToList(),
                // The ranking emits only full frames and evictions; anything else leaves the window.
                _ => window,
            })
            .Select(window => (IReadOnlyCollection<QueryResult>)window);

    /// <summary>A subtree key and whether the read it came from was complete.</summary>
    private readonly record struct SubtreeKey(object? Key, bool Incomplete, IReadOnlyList<string>? SilentProviders);

    /// <summary>
    /// The ranked state of one subscription: every candidate, its key, and the window last emitted.
    /// </summary>
    private sealed class SubtreeRanking<T>(
        MeshQueryRequest request,
        MeshQueryRequest keysRequest,
        ParsedQuery parsed,
        int maxCandidates,
        Func<MeshQueryRequest, IObservable<QueryResultChange<MeshNode>>> runNodes)
    {
        private readonly Dictionary<string, (T Item, double Score, SubtreeKey Key)> candidates =
            new(StringComparer.OrdinalIgnoreCase);
        private IReadOnlyList<T> emitted = [];
        private bool emittedIncomplete;
        private IReadOnlyList<string>? emittedSilent;
        private bool initialized;
        // Deltas that arrived before the first full frame. A multi-provider merge forwards one
        // provider's change while another is still loading; its combined Initial then carries the
        // first provider's OLDER snapshot, so the deltas are replayed over it (review #4306596210).
        private readonly List<QueryResultChange<T>> early = [];
        // The candidate read's own incompleteness, as of its last full frame.
        private bool candidatesIncomplete;
        private IReadOnlyList<string>? candidatesSilent;

        public IObservable<QueryResultChange<T>> Apply(QueryResultChange<T> change)
        {
            // 🚨 A row that is not a node has no subtree to key. Passing the batch through would hand
            // back an UNLIMITED, unranked result — the candidate read dropped skip and limit — so the
            // node-only selector refuses it by name (review #4306596197).
            if (change.Items.FirstOrDefault(item => item is not MeshNode) is { } stray)
                return Observable.Throw<QueryResultChange<T>>(new InvalidOperationException(
                    $"sort:{SubtreeSort.SubtreeLastModified} ranks mesh nodes by their descendants, and "
                    + $"'{request.EffectiveQueries.FirstOrDefault()}' returned a {stray?.GetType().Name ?? "null"} row. "
                    + "Narrow the query to nodes (a nodeType:, a namespace:) or sort by a field the row carries."));

            // 🚨 Only the merged INITIAL is a mesh-wide snapshot. The multi-provider merge combines
            // the providers' Initials but forwards a provider's later Reset on its own, so a Reset
            // names ONE provider's rows. Clearing on it dropped every other provider's containers and
            // evicted them for good — those providers never re-emit an unchanged row (review
            // #4307032191). A Reset therefore upserts its rows, like an Updated. The cost is the
            // opposite error, bounded: a row the resetting provider no longer has stays ranked until
            // its own Removed arrives, rather than every other provider's rows vanishing.
            var full = change.ChangeType == QueryChangeType.Initial;
            if (!full && !initialized)
            {
                early.Add(change);   // replayed over the first full frame
                return Observable.Empty<QueryResultChange<T>>();
            }
            if (full && !initialized && early.Count > 0)
            {
                change = change with { Items = ReplayEarly(change), Scores = null };
                early.Clear();
            }

            if (change.ChangeType == QueryChangeType.Removed)
            {
                foreach (var item in change.Items)
                    if (PathOf(item) is { } path)
                        candidates.Remove(path);
                return Window(change, forceEmit: false);
            }

            if (full)
            {
                candidates.Clear();
                candidatesIncomplete = change.SnapshotIncomplete;
                candidatesSilent = change.SilentProviders;
            }
            var hits = change.Items
                .Select((item, i) => (Item: item, Score: change.Scores is { } s && i < s.Count ? s[i] : 0.0))
                .Where(hit => PathOf(hit.Item) is not null)
                .ToList();
            var projected = full ? hits.Count : candidates.Count + hits.Count(h => !candidates.ContainsKey(PathOf(h.Item)!));
            if (projected > maxCandidates)
                return Observable.Throw<QueryResultChange<T>>(new InvalidOperationException(
                    $"sort:{SubtreeSort.SubtreeLastModified} orders at most {maxCandidates} candidates "
                    + $"(MeshOperationOptions.SubtreeSortMaxCandidates) and '{request.EffectiveQueries.FirstOrDefault()}' "
                    + $"matched {projected}. It reads one key per candidate, so it is meant for listing "
                    + "containers: narrow the query (a nodeType:, a namespace:) to the containers you mean to rank."));

            return hits
                .Select(hit => NewestDescendant((MeshNode)(object)hit.Item!, keysRequest, runNodes)
                    .Select(key => (hit.Item, hit.Score, Key: key)))
                .MergeBounded(SubtreeKeyConcurrency)
                .ToList()
                .SelectMany(keyed =>
                {
                    foreach (var (item, score, key) in keyed)
                        candidates[PathOf(item)!] = (item, score, key);
                    // An Updated row inside the window re-emits it even when the order holds: the
                    // consumer must see the row's new content.
                    var touchesWindow = !full && keyed.Any(k => emitted.Any(e => SamePath(e, k.Item)));
                    return Window(change, forceEmit: full || touchesWindow);
                });
        }

        /// <summary>
        /// The first full frame's rows with the early deltas folded over them in arrival order: an
        /// Added or Updated replaces or adds its row, a Removed takes it out.
        /// </summary>
        private IReadOnlyList<T> ReplayEarly(QueryResultChange<T> first)
        {
            var rows = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            void Put(T item)
            {
                if (PathOf(item) is not { } path)
                    return;
                if (!rows.ContainsKey(path))
                    order.Add(path);
                rows[path] = item;
            }
            foreach (var item in first.Items)
                Put(item);
            foreach (var delta in early)
                foreach (var item in delta.Items)
                    if (delta.ChangeType == QueryChangeType.Removed)
                    {
                        if (PathOf(item) is { } path)
                            rows.Remove(path);
                    }
                    else
                        Put(item);
            // Distinct: an Added, Removed, Added of one path appends it twice to `order` (review
            // #4307076427) — counted twice against the bound, keyed twice below it.
            return order.Distinct(StringComparer.OrdinalIgnoreCase).Where(rows.ContainsKey).Select(path => rows[path]).ToList();
        }

        /// <summary>
        /// Re-takes the window and emits it when its rows or its completeness changed (or when
        /// <paramref name="forceEmit"/>): an Initial for the first frame, after that a Removed for
        /// the rows that left it followed by a Reset.
        /// </summary>
        private IObservable<QueryResultChange<T>> Window(QueryResultChange<T> change, bool forceEmit)
        {
            IEnumerable<(T Item, double Score, SubtreeKey Key)> ordered = candidates.Values
                .OrderBy(c => c.Key.Key, QuerySortKey.Comparer(parsed.OrderBy!.Descending))
                // The path tiebreak makes it a TOTAL order, which skip/limit need to be paging.
                .ThenBy(c => PathOf(c.Item) ?? "", StringComparer.Ordinal);
            if (request.Skip is int skip && skip > 0)
                ordered = ordered.Skip(skip);
            if ((request.Limit ?? parsed.Limit) is int limit && limit > 0)
                ordered = ordered.Take(limit);
            var window = ordered.ToList();

            // 🚨 Never a complete-looking ranking over an incomplete read: a key read that did not
            // complete (a provider silent or SnapshotIncomplete) may have missed the newest row, so
            // the order it produced is a floor — the frame says so, as the candidate read's own does.
            // Computed BEFORE the unchanged check: an off-page key read turning incomplete leaves the
            // rows as they were and must still be reported (review #4306054499).
            var incomplete = candidatesIncomplete || candidates.Values.Any(c => c.Key.Incomplete);
            var silent = candidates.Values
                .SelectMany(c => c.Key.SilentProviders ?? [])
                .Concat(candidatesSilent ?? [])
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            IReadOnlyList<string>? silentOrNull = silent.Length == 0 ? null : silent;

            var unchanged = initialized
                && window.Count == emitted.Count
                // Same rows (by identity: an Updated row is a new instance), in the same order.
                && window.Select(w => w.Item).Zip(emitted, (a, b) => SamePath(a, b) && ReferenceEquals(a, b)).All(same => same)
                && incomplete == emittedIncomplete
                && (emittedSilent ?? []).SequenceEqual(silentOrNull ?? [], StringComparer.Ordinal);
            if (unchanged && !forceEmit)
                return Observable.Empty<QueryResultChange<T>>();

            var first = !initialized;
            initialized = true;
            var evicted = emitted.Where(e => !window.Any(w => SamePath(w.Item, e))).ToList();
            emitted = window.Select(w => w.Item).ToList();
            emittedIncomplete = incomplete;
            emittedSilent = silentOrNull;
            var frame = Observable.Return(change with
            {
                ChangeType = first ? QueryChangeType.Initial : QueryChangeType.Reset,
                Items = emitted,
                Scores = window.Select(w => w.Score).ToList(),
                Query = parsed,
                SnapshotIncomplete = incomplete,
                SilentProviders = silentOrNull,
            });
            // Evictions FIRST: a cache that folds the Reset additively would otherwise snapshot the
            // window plus the rows it pushed out, one frame over the limit.
            return evicted.Count == 0
                ? frame
                : Observable.Return(change with
                {
                    ChangeType = QueryChangeType.Removed,
                    Items = evicted,
                    Scores = null,
                    Query = parsed,
                    SnapshotIncomplete = incomplete,
                    SilentProviders = silentOrNull,
                }).Concat(frame);
        }

        private static string? PathOf(T item) => item is MeshNode { Path: { Length: > 0 } path } ? path : null;

        private static bool SamePath(T a, T b)
            => string.Equals(PathOf(a), PathOf(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The key of <paramref name="node"/>: the newest stamp among the descendants the caller's own
    /// descendants read returns, else the node's own stamp; null when neither is set. The read's
    /// incompleteness travels with the key — a silent provider or a <c>SnapshotIncomplete</c> frame,
    /// or a read that completed without answering at all.
    /// </summary>
    private static IObservable<SubtreeKey> NewestDescendant(
        MeshNode node,
        MeshQueryRequest keysRequest,
        Func<MeshQueryRequest, IObservable<QueryResultChange<MeshNode>>> runNodes)
    {
        DateTimeOffset? own = node.LastModified == default ? null : node.LastModified;
        if (string.IsNullOrEmpty(node.Path))
            return Observable.Return(new SubtreeKey(QuerySortKey.Normalize(own), false, null));
        var read = keysRequest with
        {
            Query = SubtreeSort.NewestDescendantQuery(node.Path),
            Queries = null,
            Skip = null,
            Limit = 1,
            // The candidate query's own narrowing is not the key's: activity anywhere below counts.
            Context = null,
            PartitionHint = null,
            TableHint = null,
            DefaultPath = null,
            ContextPath = null,
            ReportPartialReads = false,
        };
        return runNodes(read)
            .Where(c => c.ChangeType == QueryChangeType.Initial)
            .Take(1)
            .Select(c => new SubtreeKey(
                QuerySortKey.Normalize(c.Items
                    .Where(n => n.LastModified != default
                                && !string.Equals(n.Path, node.Path, StringComparison.OrdinalIgnoreCase))
                    .Select(n => (DateTimeOffset?)n.LastModified)
                    .Max() ?? own),
                c.SnapshotIncomplete || c.SilentProviders is { Count: > 0 },
                c.SilentProviders))
            // Completed without an Initial: nobody answered, so the fallback key is a floor.
            .DefaultIfEmpty(new SubtreeKey(QuerySortKey.Normalize(own), true, null));
    }
}
