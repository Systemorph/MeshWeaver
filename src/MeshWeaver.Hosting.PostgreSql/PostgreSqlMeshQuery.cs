using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// PostgreSQL native implementation of IMeshQueryProvider.
/// Translates parsed queries directly into PostgreSQL SQL via PostgreSqlStorageAdapter.
/// </summary>
public class PostgreSqlMeshQuery : IMeshQueryProvider
{
    private readonly PostgreSqlStorageAdapter _adapter;
    private readonly IDataChangeNotifier? _changeNotifier;
    private readonly AccessService? _accessService;
    private readonly MeshConfiguration? _meshConfiguration;
    private readonly QueryParser _parser = new();
    private long _version;

    public static readonly TimeSpan DefaultDebounceInterval = TimeSpan.FromMilliseconds(100);

    // Namespaces handled by other (static) partitions — Postgres excludes
    // them from its Matches() predicate so a `namespace:Agent` query never
    // round-trips to SQL to look for built-in agents. Populated from the
    // partition registry at DI registration time.
    private readonly HashSet<string> _excludedNamespaces;

    public PostgreSqlMeshQuery(
        PostgreSqlStorageAdapter adapter,
        IDataChangeNotifier? changeNotifier = null,
        AccessService? accessService = null,
        MeshConfiguration? meshConfiguration = null,
        IEnumerable<string>? excludedNamespaces = null)
    {
        _adapter = adapter;
        _changeNotifier = changeNotifier;
        _accessService = accessService;
        _meshConfiguration = meshConfiguration;
        _excludedNamespaces = (excludedNamespaces ?? Enumerable.Empty<string>())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public bool Matches(IReadOnlyList<string> queryNamespaces)
    {
        for (var i = 0; i < queryNamespaces.Count; i++)
            if (!_excludedNamespaces.Contains(queryNamespaces[i]))
                return true;
        return false;
    }

    /// <summary>
    /// Exposes the underlying adapter for cross-schema queries.
    /// </summary>
    public PostgreSqlStorageAdapter Adapter => _adapter;

    /// <summary>
    /// Gets the effective user ID from the request or from the current access context.
    /// Returns WellKnownUsers.Anonymous for unauthenticated/virtual access.
    /// </summary>
    private string GetEffectiveUserId(MeshQueryRequest request)
    {
        // System identity bypasses access control (infrastructure queries)
        if (request.UserId == WellKnownUsers.System)
            return "";

        if (!string.IsNullOrEmpty(request.UserId))
            return request.UserId;

        var userId = _accessService?.Context?.ObjectId
                     ?? _accessService?.CircuitContext?.ObjectId;
        return string.IsNullOrEmpty(userId) ? WellKnownUsers.Anonymous : userId;
    }

    public async IAsyncEnumerable<object> QueryAsync(
        MeshQueryRequest request,
        JsonSerializerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Multi-query union: push UNION down to PostgreSQL via the adapter so
        // the database does the dedup-by-path in a single round-trip. The
        // first query's sort/limit/skip apply to the unioned result set;
        // additional queries contribute membership only. See
        // PostgreSqlStorageAdapter.QueryNodesAsync(IReadOnlyList&lt;ParsedQuery&gt;,...).
        if (request.Queries is { Count: > 1 })
        {
            await foreach (var item in QueryNodesUnionAsync(request, options, ct))
                yield return item;
            yield break;
        }

        var parsedQuery = _parser.Parse(request.Query);

        if (request.Limit.HasValue)
            parsedQuery = parsedQuery with { Limit = request.Limit };

        parsedQuery = StripTypeFilter(parsedQuery);

        var effectivePath = parsedQuery.Path;
        var effectiveScope = parsedQuery.Scope;
        if (string.IsNullOrEmpty(effectivePath))
        {
            if (!string.IsNullOrEmpty(request.DefaultPath))
                effectivePath = request.DefaultPath;
            if (parsedQuery.Scope == QueryScope.Exact)
                effectiveScope = QueryScope.Children;
        }

        parsedQuery = parsedQuery with { Path = effectivePath, Scope = effectiveScope };

        // Context-based exclusion — push to SQL WHERE clause so the database
        // filters excluded node types instead of fetching and discarding them.
        var context = request.Context ?? parsedQuery.Context;
        var excludedNodeTypes = context != null
            ? _meshConfiguration?.GetExcludedNodeTypes(context)
            : null;

        // Determine activity user ID for source:accessed queries (JOIN with UserActivity nodes)
        var activityUserId = parsedQuery.Source == QuerySource.Accessed
            ? GetEffectiveUserId(request)
            : null;

        // When ContextPath is set, buffer results to apply proximity re-ranking
        if (!string.IsNullOrEmpty(request.ContextPath))
        {
            var buffered = new List<(MeshNode Node, double Score)>();
            var userId = GetEffectiveUserId(request);
            await foreach (var node in _adapter.QueryNodesAsync(parsedQuery, options, userId, activityUserId: activityUserId, excludedNodeTypes: excludedNodeTypes, ct: ct))
            {
                // Instance-level exclusion still checked in memory (node.ExcludeFromContext)
                if (context != null && node.ExcludeFromContext?.Contains(context) == true)
                    continue;
                var boost = PathProximity.ComputeBoost(request.ContextPath, node.Path);
                buffered.Add((node, boost));
            }

            var skip = request.Skip ?? 0;
            var count = 0;
            foreach (var (node, _) in buffered.OrderByDescending(b => b.Score))
            {
                if (skip > 0) { skip--; continue; }
                yield return parsedQuery.Select != null
                    ? ParsedQuery.ProjectToSelect(node, parsedQuery.Select)
                    : node;
                count++;
                if (parsedQuery.Limit.HasValue && count >= parsedQuery.Limit.Value)
                    yield break;
            }
            yield break;
        }

        var skipOrig = request.Skip ?? 0;
        var countOrig = 0;

        var effectiveUserId = GetEffectiveUserId(request);
        await foreach (var node in _adapter.QueryNodesAsync(parsedQuery, options, effectiveUserId, activityUserId: activityUserId, excludedNodeTypes: excludedNodeTypes, ct: ct))
        {
            // Instance-level exclusion still checked in memory (node.ExcludeFromContext)
            if (context != null && node.ExcludeFromContext?.Contains(context) == true)
                continue;

            if (skipOrig > 0)
            {
                skipOrig--;
                continue;
            }

            yield return parsedQuery.Select != null
                ? ParsedQuery.ProjectToSelect(node, parsedQuery.Select)
                : node;

            countOrig++;
            if (parsedQuery.Limit.HasValue && countOrig >= parsedQuery.Limit.Value)
                yield break;
        }
    }

    /// <summary>
    /// Multi-query union path: parses each query in <see cref="MeshQueryRequest.Queries"/>,
    /// applies the same path/scope/limit fallbacks as the single-query branch,
    /// then hands the parsed list to the adapter's UNION-emitting overload.
    /// First query's sort/limit/skip apply to the unioned result set.
    /// </summary>
    private async IAsyncEnumerable<object> QueryNodesUnionAsync(
        MeshQueryRequest request,
        JsonSerializerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var queries = request.Queries!;
        var parsedList = new List<ParsedQuery>(queries.Count);
        ParsedQuery? firstParsed = null;
        for (var qi = 0; qi < queries.Count; qi++)
        {
            // No per-query Limit injection — applying request.Limit to query #0
            // only made the union iteration-order dependent (query #0 hits its
            // limit before yielding its most relevant rows; queries #1+ then
            // contribute everything past it). Limit is enforced post-union
            // below via MinLimit so a request-level cap can't be circumvented
            // and a query-string `limit:N` still wins when smaller.
            var pq = _parser.Parse(queries[qi]);
            pq = StripTypeFilter(pq);

            var effectivePath = pq.Path;
            var effectiveScope = pq.Scope;
            if (string.IsNullOrEmpty(effectivePath))
            {
                if (!string.IsNullOrEmpty(request.DefaultPath))
                    effectivePath = request.DefaultPath;
                if (pq.Scope == QueryScope.Exact)
                    effectiveScope = QueryScope.Children;
            }
            pq = pq with { Path = effectivePath, Scope = effectiveScope };
            if (qi == 0) firstParsed = pq;
            parsedList.Add(pq);
        }

        var context = request.Context ?? firstParsed!.Context;
        var excludedNodeTypes = context != null
            ? _meshConfiguration?.GetExcludedNodeTypes(context)
            : null;
        var activityUserId = firstParsed!.Source == QuerySource.Accessed
            ? GetEffectiveUserId(request)
            : null;

        var skip = request.Skip ?? 0;
        var count = 0;
        // Effective limit = min of request-level cap and the FIRST query's
        // explicit `limit:N`. Smaller wins so a request-level cap can't be
        // bypassed by a higher in-query limit, and an explicit in-query limit
        // still applies when no request cap is set.
        var effectiveLimit = MinLimit(request.Limit, firstParsed.Limit);
        var effectiveUserId = GetEffectiveUserId(request);
        await foreach (var node in _adapter.QueryNodesAsync(
            parsedList, options, effectiveUserId, activityUserId: activityUserId,
            excludedNodeTypes: excludedNodeTypes, ct: ct))
        {
            if (context != null && node.ExcludeFromContext?.Contains(context) == true)
                continue;
            if (skip > 0) { skip--; continue; }

            yield return firstParsed.Select != null
                ? ParsedQuery.ProjectToSelect(node, firstParsed.Select)
                : node;
            count++;
            if (effectiveLimit.HasValue && count >= effectiveLimit.Value)
                yield break;
        }
    }

    private static int? MinLimit(int? a, int? b) =>
        (a, b) switch
        {
            (null, null) => null,
            (null, var v) => v,
            (var v, null) => v,
            ({ } x, { } y) => Math.Min(x, y)
        };

    public IAsyncEnumerable<QuerySuggestion> AutocompleteAsync(
        string basePath,
        string prefix,
        JsonSerializerOptions options,
        int limit = 10,
        CancellationToken ct = default)
        => AutocompleteAsync(basePath, prefix, options, AutocompleteMode.PathFirst, limit, null, null, ct);

    public async IAsyncEnumerable<QuerySuggestion> AutocompleteAsync(
        string basePath,
        string prefix,
        JsonSerializerOptions options,
        AutocompleteMode mode,
        int limit = 10,
        string? contextPath = null,
        string? context = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var normalizedPrefix = (prefix ?? "").ToLowerInvariant();

        // Use ILIKE-based filter instead of plainto_tsquery for substring prefix matching.
        // plainto_tsquery requires full words; ILIKE matches partial prefixes (e.g., "mar" matches "Markdown").
        QueryNode? filter = null;
        if (!string.IsNullOrEmpty(normalizedPrefix))
        {
            filter = new QueryOr([
                new QueryComparison(new QueryCondition("name", QueryOperator.Like, [normalizedPrefix])),
                new QueryComparison(new QueryCondition("path", QueryOperator.Like, [normalizedPrefix])),
                new QueryComparison(new QueryCondition("description", QueryOperator.Like, [normalizedPrefix])),
                new QueryComparison(new QueryCondition("nodeType", QueryOperator.Like, [normalizedPrefix])),
            ]);
        }

        var query = new ParsedQuery(
            Filter: filter,
            TextSearch: null,
            Path: basePath,
            Scope: QueryScope.Descendants);

        var suggestions = new List<QuerySuggestion>();

        var acUserId = _accessService?.Context?.ObjectId;
        var effectiveAutocompleteUserId = string.IsNullOrEmpty(acUserId) ? WellKnownUsers.Anonymous : acUserId;

        await foreach (var node in _adapter.QueryNodesAsync(query, options, effectiveAutocompleteUserId, ct: ct))
        {
            // Skip node types excluded from autocomplete (configured via AddAutocompleteExcludedTypes)
            if (_meshConfiguration?.AutocompleteExcludedNodeTypes.Contains(node.NodeType ?? "") == true)
                continue;

            // Context-based exclusion for autocomplete
            if (context != null)
            {
                if (_meshConfiguration?.IsExcludedFromContext(node.NodeType, context) == true)
                    continue;
                if (node.ExcludeFromContext?.Contains(context) == true)
                    continue;
            }

            var name = node.Name ?? node.Id ?? node.Path ?? "";
            double score = 0;

            if (name.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
                score = 100 - (name.Length - normalizedPrefix.Length);
            else if (name.Contains(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
                score = 50;
            else if ((node.Path ?? "").Contains(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
                score = 30;

            score += PathProximity.ComputeBoost(contextPath, node.Path);

            if (score > 0 || string.IsNullOrEmpty(normalizedPrefix))
                suggestions.Add(new QuerySuggestion(node.Path ?? "", name, node.NodeType, score, node.Icon));
        }

        IEnumerable<QuerySuggestion> ordered = mode switch
        {
            AutocompleteMode.RelevanceFirst => suggestions
                .OrderByDescending(s => s.Score)
                .ThenBy(s => s.Path.Length)
                .ThenBy(s => s.Name),
            _ => suggestions
                .OrderBy(s => s.Path.Length)
                .ThenByDescending(s => s.Score)
                .ThenBy(s => s.Name)
        };

        foreach (var suggestion in ordered.Take(limit))
        {
            yield return suggestion;
        }
    }

    /// <inheritdoc />
    public async Task<T?> SelectAsync<T>(string path, string property, JsonSerializerOptions options, CancellationToken ct = default)
    {
        var query = new ParsedQuery(
            Filter: new QueryComparison(new QueryCondition("path", QueryOperator.Equal, [path])),
            TextSearch: null,
            Path: null,
            Scope: QueryScope.Exact);

        var acUserId = _accessService?.Context?.ObjectId;
        var effectiveSelectUserId = string.IsNullOrEmpty(acUserId) ? WellKnownUsers.Anonymous : acUserId;

        await foreach (var node in _adapter.QueryNodesAsync(query, options, effectiveSelectUserId, ct: ct))
        {
            var prop = typeof(MeshNode).GetProperty(property);
            if (prop == null)
                return default;

            var value = prop.GetValue(node);
            if (value is T typedValue)
                return typedValue;

            return default;
        }

        return default;
    }

    public IObservable<QueryResultChange<T>> ObserveQuery<T>(MeshQueryRequest request, JsonSerializerOptions options)
    {
        // Use the synchronous Observable.Create overload so no TaskScheduler is captured
        // at subscribe-time. Observable.Create(async ...) captures the caller's scheduler;
        // when that caller is an Orleans grain handler the continuation deadlocks against
        // the grain's single-threaded scheduler.
        return Observable.Create<QueryResultChange<T>>(observer =>
        {
            // Multi-query union: parse EVERY query and OR-join their (basePath, scope)
            // change-notifier filters. Without this, only query #0's path/scope drives
            // delta refresh — matches against query #1+ silently never trigger a re-run.
            // The Initial snapshot itself uses request.Queries via QueryAsync, but the
            // change-detection layer must observe the union of all branches' shapes.
            var effectiveQueries = request.EffectiveQueries;
            var parsedFilters = new List<(string BasePath, QueryScope Scope)>(effectiveQueries.Count);
            ParsedQuery firstParsed = null!;
            for (var qi = 0; qi < effectiveQueries.Count; qi++)
            {
                var pq = _parser.Parse(effectiveQueries[qi]);
                var effectivePath = pq.Path;
                var effectiveScope = pq.Scope;
                if (string.IsNullOrEmpty(effectivePath))
                {
                    effectivePath = request.DefaultPath ?? "";
                    if (pq.Scope == QueryScope.Exact)
                        effectiveScope = QueryScope.Children;
                }
                var normalizedBasePath = effectivePath?.Trim('/') ?? "";
                parsedFilters.Add((normalizedBasePath, effectiveScope));
                if (qi == 0) firstParsed = pq;
            }

            var currentItems = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
            var disposables = new CompositeDisposable();

            // Observable.FromAsync with Scheduler.Default defers all DB work to the
            // ThreadPool — no custom TaskScheduler (Orleans) is ever captured.
            // CollectQueryResultsAsync is the sole async boundary (persistence layer).
            IObservable<List<(string? Path, T Item)>> RunQuery()
                => Observable.FromAsync(ct => CollectQueryResultsAsync<T>(request, options, ct), Scheduler.Default);

            // Race-fix (mirrors StorageAdapterMeshQueryProvider): subscribe to
            // changeNotifier BEFORE running the initial query so that any
            // pg_notify-driven NotifyChange fired during the initial query's
            // I/O window is captured in a backlog. Without this, events fired
            // between RunQuery's persistence read and the live-subscription
            // setup are dropped — DataChangeNotifier is a plain Subject<> with
            // no buffering. Symptom: synced query consumers (e.g.
            // EffectivePermissionPostgresTest.RuntimeCreateNode_AccessAssignment_PgBacked_GrantsPermission)
            // never see writes that complete during the first Initial query.
            //
            // Approach: accumulate early notifications under a lock until the
            // initial query completes. Inside the initialResults callback:
            //   1) Set up the LIVE Buffer(100ms) pipeline first (captures live events).
            //   2) Snapshot+clear the backlog under lock; set initialDone=true.
            //   3) Dispose the early subscription (live carries everything now).
            //   4) Emit Initial.
            //   5) Drain the backlog as one synthetic batch by re-querying and
            //      diffing against the just-populated currentItems via ProcessBatch.
            // Events fired between live-set-up (step 1) and backlog-swap (step 2)
            // may be double-captured — ProcessBatch is idempotent against
            // currentItems, so duplicate processing is wasted CPU but correct.
            var earlyBacklog = new List<DataChangeNotification>();
            var earlyLock = new object();
            var initialDone = false;

            IDisposable? earlySubscription = null;
            if (_changeNotifier != null)
            {
                earlySubscription = _changeNotifier
                    .Where(n => parsedFilters.Any(f =>
                        PathMatcher.ShouldNotify(n.Path, f.BasePath, f.Scope)))
                    .Subscribe(n =>
                    {
                        lock (earlyLock)
                        {
                            if (!initialDone)
                                earlyBacklog.Add(n);
                        }
                    });
                disposables.Add(earlySubscription);
            }

            disposables.Add(RunQuery().Subscribe(
                initialResults =>
                {
                    var initialItems = new List<T>();
                    foreach (var (path, item) in initialResults)
                    {
                        initialItems.Add(item);
                        if (!string.IsNullOrEmpty(path))
                            currentItems[path] = item;
                    }

                    DataChangeNotification[] backlog = Array.Empty<DataChangeNotification>();
                    if (_changeNotifier != null)
                    {
                        // 1) Set up live subscription first — starts buffering immediately.
                        var changeBuffer = new Subject<DataChangeNotification>();
                        disposables.Add(changeBuffer);
                        disposables.Add(
                            _changeNotifier
                                .Where(n => parsedFilters.Any(f =>
                                    PathMatcher.ShouldNotify(n.Path, f.BasePath, f.Scope)))
                                .Subscribe(changeBuffer));
                        disposables.Add(
                            changeBuffer
                                .Buffer(DefaultDebounceInterval)
                                .Where(batch => batch.Count > 0)
                                .Subscribe(batch =>
                                    disposables.Add(
                                        RunQuery().Subscribe(
                                            newResults => ProcessBatch(batch, newResults, currentItems, firstParsed, observer),
                                            ex => observer.OnError(ex)))));

                        // 2) Snapshot + clear early backlog under lock; gate further early-capture.
                        lock (earlyLock)
                        {
                            backlog = earlyBacklog.ToArray();
                            earlyBacklog.Clear();
                            initialDone = true;
                        }

                        // 3) Early subscription is now redundant — live pipeline carries
                        //    all subsequent events. Dispose to free the upstream sub.
                        earlySubscription?.Dispose();
                    }

                    observer.OnNext(new QueryResultChange<T>
                    {
                        ChangeType = QueryChangeType.Initial,
                        Items = initialItems,
                        Scores = ComputeRowScores<T>(initialItems, firstParsed, request),
                        Query = firstParsed,
                        Version = Interlocked.Increment(ref _version),
                        Timestamp = DateTimeOffset.UtcNow
                    });

                    // Drain the early backlog as one immediate batch — these events
                    // fired DURING the initial query window, so we re-query and
                    // apply diffs against the just-populated currentItems.
                    if (backlog.Length > 0)
                    {
                        disposables.Add(
                            RunQuery().Subscribe(
                                newResults => ProcessBatch(backlog.ToList(), newResults, currentItems, firstParsed, observer),
                                ex => observer.OnError(ex)));
                    }

                    if (_changeNotifier == null)
                        observer.OnCompleted();
                },
                ex => observer.OnError(ex)));

            return disposables;
        });
    }

    private void ProcessBatch<T>(
        IList<DataChangeNotification> batch,
        List<(string? Path, T Item)> newResults,
        Dictionary<string, T> currentItems,
        ParsedQuery parsedQuery,
        IObserver<QueryResultChange<T>> observer)
    {
        var newItems = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, item) in newResults)
            if (!string.IsNullOrEmpty(path))
                newItems[path] = item;

        var addedItems = new List<T>();
        var updatedItems = new List<T>();
        var removedItems = new List<T>();

        foreach (var (path, item) in newItems)
        {
            if (currentItems.ContainsKey(path))
                updatedItems.Add(item);
            else
                addedItems.Add(item);
        }
        foreach (var (path, item) in currentItems)
        {
            if (!newItems.ContainsKey(path))
                removedItems.Add(item);
        }

        currentItems.Clear();
        foreach (var (p, v) in newItems) currentItems[p] = v;

        void Emit(QueryChangeType type, IReadOnlyList<T> items)
        {
            if (items.Count == 0) return;
            // Live-delta emissions don't carry scores: the aggregator's
            // delta-path doesn't re-sort, it just merges. Initial scoring
            // already established the relative order — subsequent Added/
            // Updated/Removed flow through unchanged so consumers see the
            // event shape, not a fresh ranking. (If a re-rank is needed
            // the consumer can re-subscribe to get a new Initial.)
            observer.OnNext(new QueryResultChange<T>
            {
                ChangeType = type,
                Items = items,
                Query = parsedQuery,
                Version = Interlocked.Increment(ref _version),
                Timestamp = DateTimeOffset.UtcNow
            });
        }
        Emit(QueryChangeType.Added, addedItems);
        Emit(QueryChangeType.Updated, updatedItems);
        Emit(QueryChangeType.Removed, removedItems);
    }

    /// <summary>
    /// Per-item scoring for ObserveQuery initial emissions. Composes the same
    /// pieces <see cref="AutocompleteAsync"/> already uses — name-prefix bonus
    /// (100, scaled by length), name-substring bonus (50), path-substring
    /// bonus (30), <see cref="PathProximity.ComputeBoost"/> (max 40, decays
    /// with namespace distance from the requesting hub) — so cross-provider
    /// sort in <c>MeshQuery.ClipMergedInitial</c> ranks a PG name-prefix hit
    /// above a <c>StaticNodeQueryProvider</c> filter-only hit on the same
    /// query.
    ///
    /// <para>Returns <see langword="null"/> when there's no useful signal
    /// (non-MeshNode T, no text-search term AND no context path) so the
    /// aggregator falls back to insertion order rather than amplifying a
    /// constant 0.</para>
    /// </summary>
    private static IReadOnlyList<double>? ComputeRowScores<T>(
        IReadOnlyList<T> items, ParsedQuery parsed, MeshQueryRequest request)
    {
        if (items.Count == 0) return null;
        var contextPath = request.Context;
        var textSearch = parsed.TextSearch;
        // Bail out when no scoring dimension applies. PathProximity is
        // contextPath-driven; the text bonuses are textSearch-driven.
        if (string.IsNullOrEmpty(textSearch) && string.IsNullOrEmpty(contextPath))
            return null;
        var lowerSearch = textSearch?.ToLowerInvariant();
        var scores = new double[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] is not MeshNode node) { scores[i] = 0; continue; }
            double s = 0;
            if (!string.IsNullOrEmpty(lowerSearch))
            {
                var name = node.Name ?? node.Id ?? node.Path ?? string.Empty;
                if (name.StartsWith(lowerSearch, StringComparison.OrdinalIgnoreCase))
                    s = 100 - (name.Length - lowerSearch.Length);
                else if (name.Contains(lowerSearch, StringComparison.OrdinalIgnoreCase))
                    s = 50;
                else if ((node.Path ?? "").Contains(lowerSearch, StringComparison.OrdinalIgnoreCase))
                    s = 30;
            }
            if (!string.IsNullOrEmpty(contextPath))
                s += PathProximity.ComputeBoost(contextPath, node.Path);
            scores[i] = s;
        }
        return scores;
    }

    /// <summary>
    /// Checks whether a node should be excluded based on context.
    /// </summary>
    private bool IsExcludedByContext(MeshNode node, string? context)
    {
        if (context == null) return false;
        if (_meshConfiguration?.IsExcludedFromContext(node.NodeType, context) == true)
            return true;
        if (node.ExcludeFromContext?.Contains(context) == true)
            return true;
        return false;
    }

    private static ParsedQuery StripTypeFilter(ParsedQuery query)
    {
        if (query.Filter == null)
            return query;

        var stripped = StripTypeFromNode(query.Filter);
        return query with { Filter = stripped };
    }

    private static QueryNode? StripTypeFromNode(QueryNode node)
    {
        return node switch
        {
            QueryComparison comparison when comparison.Condition.Selector == "$type" => null,
            QueryComparison => node,
            QueryAnd and => StripTypeFromAnd(and),
            QueryOr or => StripTypeFromOr(or),
            _ => node
        };
    }

    private static QueryNode? StripTypeFromAnd(QueryAnd and)
    {
        var remaining = and.Children
            .Select(StripTypeFromNode)
            .Where(n => n != null)
            .ToList();

        return remaining.Count switch
        {
            0 => null,
            1 => remaining[0],
            _ => new QueryAnd(remaining!)
        };
    }

    private static QueryNode? StripTypeFromOr(QueryOr or)
    {
        var remaining = or.Children
            .Select(StripTypeFromNode)
            .Where(n => n != null)
            .ToList();

        return remaining.Count switch
        {
            0 => null,
            1 => remaining[0],
            _ => new QueryOr(remaining!)
        };
    }

    /// <summary>
    /// Persistence-layer async boundary: collects all results from <see cref="QueryAsync"/>
    /// into a list. Called exclusively via
    /// <see cref="Observable.FromAsync{T}(Func{CancellationToken,Task{T}},IScheduler)"/>
    /// with <see cref="Scheduler.Default"/> so <c>await</c> always runs on the
    /// ThreadPool — no hub/Orleans scheduler is ever captured.
    /// </summary>
    private async Task<List<(string? Path, T Item)>> CollectQueryResultsAsync<T>(
        MeshQueryRequest request, JsonSerializerOptions options, CancellationToken ct)
    {
        var results = new List<(string?, T)>();
        await foreach (var item in QueryAsync(request, options, ct))
            if (item is T typed)
                results.Add(((item as MeshNode)?.Path, typed));
        return results;
    }
}
