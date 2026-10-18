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
    /// membership, its order or one of its rows changed. A Reset replaces the result in every
    /// consumer, so no row outside the window is ever emitted, and the order survives.</para>
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
        // One ranking per SUBSCRIPTION (Defer), and Concat processes one change at a time, so the
        // ranking's state is only ever touched by one change's continuation. Concat also keeps the
        // stream's order: a delta arriving while keys are read waits behind the frame before it.
        return Observable.Defer(() =>
        {
            var ranking = new SubtreeRanking<T>(candidatesRequest, keysRequest, parsed, maxCandidates, runNodes);
            return run(candidates)
                .Select(ranking.Apply)
                .Concat();
        });
    }

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
        private bool initialized;
        // The candidate read's own incompleteness, as of its last full frame.
        private bool candidatesIncomplete;
        private IReadOnlyList<string>? candidatesSilent;

        public IObservable<QueryResultChange<T>> Apply(QueryResultChange<T> change)
        {
            // A batch that is not all nodes has nothing to key (the same guard ClipMergedInitial uses).
            if (change.Items.Any(item => item is not MeshNode))
                return Observable.Return(change);

            var full = change.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset;
            if (!full && !initialized)
                return Observable.Empty<QueryResultChange<T>>();   // a delta before any frame has no window to change

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
        /// Re-takes the window and emits it when it changed (or when <paramref name="forceEmit"/>):
        /// an Initial for the first frame, a Reset after that.
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

            var unchanged = initialized
                && window.Count == emitted.Count
                // Same rows (by identity: an Updated row is a new instance), in the same order.
                && window.Select(w => w.Item).Zip(emitted, (a, b) => SamePath(a, b) && ReferenceEquals(a, b)).All(same => same);
            if (unchanged && !forceEmit)
                return Observable.Empty<QueryResultChange<T>>();

            var first = !initialized;
            initialized = true;
            emitted = window.Select(w => w.Item).ToList();
            // 🚨 Never a complete-looking ranking over an incomplete read: a key read that did not
            // complete (a provider silent or SnapshotIncomplete) may have missed the newest row, so
            // the order it produced is a floor — the frame says so, as the candidate read's own does.
            var keyIncomplete = candidates.Values.Any(c => c.Key.Incomplete);
            var silent = candidates.Values
                .SelectMany(c => c.Key.SilentProviders ?? [])
                .Concat(candidatesSilent ?? [])
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return Observable.Return(change with
            {
                ChangeType = first ? QueryChangeType.Initial : QueryChangeType.Reset,
                Items = emitted,
                Scores = window.Select(w => w.Score).ToList(),
                Query = parsed,
                SnapshotIncomplete = candidatesIncomplete || keyIncomplete,
                SilentProviders = silent.Length == 0 ? null : silent,
            });
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
