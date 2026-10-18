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
    /// Runs <paramref name="candidatesRequest"/> ordered by the subtree key, reading the keys as <paramref name="keysRequest"/>. <paramref name="run"/> and
    /// <paramref name="runNodes"/> are the surface the caller entered by (secured or core), so the
    /// candidate read and the key reads take the same route and the same identity.
    /// </summary>
    private IObservable<QueryResultChange<T>> OrderBySubtreeActivity<T>(
        MeshQueryRequest candidatesRequest,
        MeshQueryRequest keysRequest,
        ParsedQuery parsed,
        Func<MeshQueryRequest, IObservable<QueryResultChange<T>>> run,
        Func<MeshQueryRequest, IObservable<QueryResultChange<MeshNode>>> runNodes)
    {
        // The candidates: the same query with the selector and the window removed. NoLimit
        // overrides a `limit:N` token as well; the window is applied after the keys are known.
        var candidates = candidatesRequest with
        {
            Query = SubtreeSort.WithoutSort(candidatesRequest.Query),
            Queries = candidatesRequest.Queries?.Select(SubtreeSort.WithoutSort).ToList(),
            Skip = null,
            Limit = MeshQueryRequest.NoLimit,
        };
        var maxCandidates = OperationOptions.SubtreeSortMaxCandidates;
        // Concat keeps the stream's order: a live delta that arrives while the Initial's keys are
        // being read waits behind the ranked Initial instead of overtaking it.
        return run(candidates)
            .Select(change => change.ChangeType == QueryChangeType.Initial
                ? RankBySubtreeKey(change, candidatesRequest, keysRequest, parsed, maxCandidates, runNodes)
                : Observable.Return(change))
            .Concat();
    }

    private static IObservable<QueryResultChange<T>> RankBySubtreeKey<T>(
        QueryResultChange<T> change,
        MeshQueryRequest request,
        MeshQueryRequest keysRequest,
        ParsedQuery parsed,
        int maxCandidates,
        Func<MeshQueryRequest, IObservable<QueryResultChange<MeshNode>>> runNodes)
    {
        // A batch that is not all nodes has nothing to key (the same guard ClipMergedInitial uses).
        if (change.Items.Any(item => item is not MeshNode))
            return Observable.Return(change);
        if (change.Items.Count > maxCandidates)
            return Observable.Throw<QueryResultChange<T>>(new InvalidOperationException(
                $"sort:{SubtreeSort.SubtreeLastModified} orders at most {maxCandidates} candidates "
                + $"(MeshOperationOptions.SubtreeSortMaxCandidates) and '{request.EffectiveQueries.FirstOrDefault()}' "
                + $"matched {change.Items.Count}. It reads one key per candidate, so it is meant for listing "
                + "containers: narrow the query (a nodeType:, a namespace:) to the containers you mean to rank."));

        var hits = change.Items
            .Select((item, i) => (Item: item, Score: change.Scores is { } s && i < s.Count ? s[i] : 0.0))
            .ToList();
        var orderBy = parsed.OrderBy!;
        return hits
            .Select(hit => NewestDescendant((MeshNode)(object)hit.Item!, keysRequest, runNodes)
                .Select(key => (hit.Item, hit.Score, Key: QuerySortKey.Normalize(key))))
            .MergeBounded(SubtreeKeyConcurrency)
            .ToList()
            .Select(keyed =>
            {
                IEnumerable<(T Item, double Score, object? Key)> ordered = keyed
                    .OrderBy(k => k.Key, QuerySortKey.Comparer(orderBy.Descending))
                    // The path tiebreak makes it a TOTAL order, which skip/limit need to be paging.
                    .ThenBy(k => ((MeshNode)(object)k.Item!).Path ?? "", StringComparer.Ordinal);
                if (request.Skip is int skip && skip > 0)
                    ordered = ordered.Skip(skip);
                if ((request.Limit ?? parsed.Limit) is int limit && limit > 0)
                    ordered = ordered.Take(limit);
                var window = ordered.ToList();
                return change with
                {
                    Items = window.Select(k => k.Item).ToList(),
                    Scores = window.Select(k => k.Score).ToList(),
                    Query = parsed,
                };
            });
    }

    /// <summary>
    /// The key of <paramref name="node"/>: the newest stamp among the descendants the caller's own
    /// descendants read returns, else the node's own stamp; null when neither is set.
    /// </summary>
    private static IObservable<DateTimeOffset?> NewestDescendant(
        MeshNode node,
        MeshQueryRequest keysRequest,
        Func<MeshQueryRequest, IObservable<QueryResultChange<MeshNode>>> runNodes)
    {
        DateTimeOffset? own = node.LastModified == default ? null : node.LastModified;
        if (string.IsNullOrEmpty(node.Path))
            return Observable.Return(own);
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
            .Select(c => c.Items
                .Where(n => n.LastModified != default
                            && !string.Equals(n.Path, node.Path, StringComparison.OrdinalIgnoreCase))
                .Select(n => (DateTimeOffset?)n.LastModified)
                .Max() ?? own)
            .DefaultIfEmpty(own);
    }
}
