using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Reactive;

namespace MeshWeaver.Hosting.Cosmos;

/// <summary>
/// Cosmos DB native implementation of IMeshQueryProvider.
/// Translates parsed queries directly into Cosmos SQL via CosmosStorageAdapter.QueryNodesAsync,
/// bypassing the in-memory persistence layer for much better performance and reliability.
/// </summary>
public class CosmosMeshQuery : IMeshQueryProvider
{
    private readonly CosmosStorageAdapter _adapter;
    private readonly MeshConfiguration? _meshConfiguration;
    private readonly HashSet<string> _excludedNamespaces;
    private readonly QueryParser _parser = new();
    private long _version;
    // Cosmos queries run inside the I/O pool (Invoke), never a bare _ioPool.Invoke.
    private readonly IIoPool _ioPool;

    /// <summary>
    /// Default interval used to debounce change-feed-driven query re-runs.
    /// </summary>
    public static readonly TimeSpan DefaultDebounceInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Initializes a new instance of the <see cref="CosmosMeshQuery"/> class.
    /// </summary>
    /// <param name="adapter">The Cosmos storage adapter that executes the underlying SQL queries.</param>
    /// <param name="meshConfiguration">Optional mesh configuration controlling context and autocomplete exclusions.</param>
    /// <param name="excludedNamespaces">Namespaces owned by static providers that this Cosmos provider should not query.</param>
    /// <param name="ioPool">Optional I/O pool used to run Cosmos round-trips off the calling scheduler; defaults to the unbounded pool.</param>
    public CosmosMeshQuery(
        CosmosStorageAdapter adapter,
        MeshConfiguration? meshConfiguration = null,
        IEnumerable<string>? excludedNamespaces = null,
        IIoPool? ioPool = null)
    {
        _adapter = adapter;
        _meshConfiguration = meshConfiguration;
        _ioPool = ioPool ?? IoPool.Unbounded;
        _excludedNamespaces = (excludedNamespaces ?? Enumerable.Empty<string>())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public bool Matches(IReadOnlyList<string> queryNamespaces)
    {
        for (var i = 0; i < queryNamespaces.Count; i++)
            if (!_excludedNamespaces.Contains(queryNamespaces[i]))
                return true;
        return false;
    }

    /// <summary>
    /// True when every namespace the request targets is owned by a static
    /// partition — Cosmos has nothing to contribute and should yield break
    /// instead of issuing a query. See PostgreSqlMeshQuery for the matching
    /// pattern; the duplication is intentional because the aggregator
    /// (MeshQuery.SelectMatchingProviders) doesn't pre-filter by Matches().
    /// </summary>
    private bool OnlyTargetsExcludedNamespaces(MeshQueryRequest request)
    {
        if (request.Queries is { Count: > 0 } queries)
        {
            foreach (var q in queries)
                if (!QueryIsExcludedOnly(q)) return false;
            return true;
        }
        return QueryIsExcludedOnly(request.Query);
    }

    private bool QueryIsExcludedOnly(string? query)
    {
        if (string.IsNullOrEmpty(query)) return false;
        var parsed = _parser.Parse(query);
        var namespaces = parsed.ExtractNamespaces();
        var firstSegment = string.IsNullOrEmpty(parsed.Path) ? null : parsed.Path.Split('/', 2)[0];
        if (namespaces.Count == 0 && string.IsNullOrEmpty(firstSegment))
            return false;
        for (var i = 0; i < namespaces.Count; i++)
            if (!_excludedNamespaces.Contains(namespaces[i]))
                return false;
        if (!string.IsNullOrEmpty(firstSegment) && !_excludedNamespaces.Contains(firstSegment))
            return false;
        return true;
    }

    /// <summary>
    /// Persistence-layer async boundary: the single async-enumerable pump over
    /// the Cosmos feed iterator. PRIVATE by design — it may only ever be
    /// enumerated from inside an <see cref="IIoPool"/> bridge
    /// (<see cref="CollectQueryResultsAsync{T}"/>, <see cref="Autocomplete"/>,
    /// <see cref="Select{T}"/>), never handed to a caller whose
    /// <c>await foreach</c> would pump it on a hub/grain scheduler.
    /// </summary>
    private async IAsyncEnumerable<object> QueryAsync(
        MeshQueryRequest request,
        JsonSerializerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Self-filter — MeshQuery's aggregator fans every provider out for
        // every query regardless of Matches(). When the request only targets
        // static-owned namespaces, we'd otherwise round-trip to Cosmos for a
        // guaranteed-empty query.
        if (_excludedNamespaces.Count > 0 && OnlyTargetsExcludedNamespaces(request))
            yield break;

        var parsedQuery = _parser.Parse(request.Query);

        // Override limit from request if provided
        if (request.Limit.HasValue)
        {
            parsedQuery = parsedQuery with { Limit = request.Limit };
        }

        // Strip $type filters — all items in the nodes container are MeshNodes,
        // so $type:MeshNode is always true and other $type values always false.
        parsedQuery = StripTypeFilter(parsedQuery);

        // Apply default path/scope logic (same as StorageAdapterMeshQueryProvider)
        var effectivePath = parsedQuery.Path;
        var effectiveScope = parsedQuery.Scope;
        if (string.IsNullOrEmpty(effectivePath))
        {
            if (!string.IsNullOrEmpty(request.DefaultPath))
            {
                effectivePath = request.DefaultPath;
            }
            if (parsedQuery.Scope == QueryScope.Exact)
            {
                effectiveScope = QueryScope.Children;
            }
        }

        // Build the final parsed query with effective path/scope
        parsedQuery = parsedQuery with { Path = effectivePath, Scope = effectiveScope };

        // Context-based exclusion
        var context = request.Context ?? parsedQuery.Context;

        // When ContextPath is set, buffer results to apply proximity re-ranking
        if (!string.IsNullOrEmpty(request.ContextPath))
        {
            var buffered = new List<(MeshNode Node, double Score)>();
            await foreach (var node in _adapter.QueryNodesAsync(parsedQuery, ct: ct).ConfigureAwait(false))
            {
                if (context != null && IsExcludedByContext(node, context))
                    continue;
                if (parsedQuery.IsMain == true && node.MainNode != node.Path)
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

        await foreach (var node in _adapter.QueryNodesAsync(parsedQuery, ct: ct).ConfigureAwait(false))
        {
            if (context != null && IsExcludedByContext(node, context))
                continue;
            if (parsedQuery.IsMain == true && node.MainNode != node.Path)
                continue;

            // Apply skip for paging
            if (skipOrig > 0)
            {
                skipOrig--;
                continue;
            }

            yield return parsedQuery.Select != null
                ? ParsedQuery.ProjectToSelect(node, parsedQuery.Select)
                : node;

            // Apply limit
            countOrig++;
            if (parsedQuery.Limit.HasValue && countOrig >= parsedQuery.Limit.Value)
                yield break;
        }
    }

    /// <inheritdoc />
    /// <summary>
    /// Native reactive autocomplete. The Cosmos execute-query (<c>_adapter.QueryNodesAsync</c> —
    /// the <c>await foreach</c> over the Cosmos feed iterator) is the I/O leaf: it runs inside
    /// <c>IIoPool.Invoke</c> and is
    /// pushed to <see cref="System.Reactive.Concurrency.TaskPoolScheduler"/> so the calling hub's
    /// action block is never blocked. No <c>Task.Run</c> bridge, no async-enumerable on the surface.
    /// </summary>
    public IObservable<IReadOnlyCollection<QueryResult>> Autocomplete(
        string basePath,
        string prefix,
        JsonSerializerOptions options,
        AutocompleteMode mode = AutocompleteMode.RelevanceFirst,
        int limit = 10,
        string? contextPath = null,
        string? context = null)
    {
        var providerName = ((IMeshQueryProvider)this).Name;
        var normalizedPrefix = (prefix ?? "").ToLowerInvariant();

        // Use descendants scope from basePath to find matching nodes
        var query = new ParsedQuery(
            Filter: null,
            TextSearch: string.IsNullOrEmpty(normalizedPrefix) ? null : normalizedPrefix,
            Path: basePath,
            Scope: QueryScope.Descendants);

        return _ioPool.Invoke(async cancel =>
            {
                var suggestions = new List<QuerySuggestion>();
                await foreach (var node in _adapter.QueryNodesAsync(query, ct: cancel).ConfigureAwait(false))
                {
                    if (_meshConfiguration?.AutocompleteExcludedNodeTypes.Contains(node.NodeType ?? "") == true)
                        continue;
                    if (context != null)
                    {
                        if (_meshConfiguration?.IsExcludedFromContext(node.NodeType, context) == true) continue;
                        if (node.IsExcludedFromContext(context)) continue;
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
                        .OrderByDescending(s => s.Score).ThenBy(s => s.Path.Length).ThenBy(s => s.Name),
                    _ => suggestions
                        .OrderBy(s => s.Path.Length).ThenByDescending(s => s.Score).ThenBy(s => s.Name)
                };

                return (IReadOnlyCollection<QueryResult>)ordered.Take(limit).Select(s => new QueryResult
                {
                    Path = s.Path,
                    Name = s.Name,
                    NodeType = s.NodeType,
                    Icon = s.Icon,
                    Score = s.Score,
                    ProviderName = providerName,
                }).ToList();
            })
            .SubscribeOn(System.Reactive.Concurrency.TaskPoolScheduler.Default);
    }

    /// <inheritdoc />
    public IObservable<T?> Select<T>(string path, string property, JsonSerializerOptions options)
    {
        var query = new ParsedQuery(
            Filter: new QueryComparison(new QueryCondition("path", QueryOperator.Equal, [path])),
            TextSearch: null,
            Path: null,
            Scope: QueryScope.Exact);

        return _ioPool.Invoke<T?>(async cancel =>
            {
                await foreach (var node in _adapter.QueryNodesAsync(query, ct: cancel).ConfigureAwait(false))
                {
                    if (typeof(MeshNode).GetProperty(property)?.GetValue(node) is T typedValue)
                        return typedValue;
                    return default;
                }
                return default;
            })
            .SubscribeOn(System.Reactive.Concurrency.TaskPoolScheduler.Default);
    }

    /// <inheritdoc />
    public IObservable<QueryResultChange<T>> Query<T>(MeshQueryRequest request, JsonSerializerOptions options)
    {
        // Self-filter: when the request only targets static-owned namespaces,
        // emit an empty Initial and exit — the static-node provider contributes
        // the rows. Without this, Cosmos would set up a watcher and issue an
        // initial query for guaranteed-empty results. Empty Initial is required
        // (MergeProviderObservables in MeshQuery gates merged Initial on every
        // provider emitting it).
        if (_excludedNamespaces.Count > 0 && OnlyTargetsExcludedNamespaces(request))
        {
            return Observable.Return(new QueryResultChange<T>
            {
                ChangeType = QueryChangeType.Initial,
                Items = Array.Empty<T>()
            });
        }

        // Use the SYNCHRONOUS Observable.Create overload so no scheduler /
        // SynchronizationContext is captured at subscribe-time. The previous
        // shape — Observable.Create(async (observer, ct) => { await foreach
        // (… QueryAsync …) }) — started the Cosmos pump ON THE SUBSCRIBER'S
        // thread and let its continuations land on the subscriber's captured
        // scheduler; when that subscriber is a hub/grain action block waiting
        // on the result, the pump queues behind the blocked thread and the
        // Initial never arrives (the grain wedge / dropped-initial flake).
        // Mirrors PostgreSqlMeshQuery.Query<T>: the DB pump runs INSIDE the
        // IIoPool, the change feed re-runs the pooled query per batch.
        return Observable.Create<QueryResultChange<T>>(observer =>
        {
            var parsedQuery = _parser.Parse(request.Query);

            var effectivePath = parsedQuery.Path;
            var effectiveScope = parsedQuery.Scope;
            if (string.IsNullOrEmpty(effectivePath))
            {
                effectivePath = request.DefaultPath ?? "";
                if (parsedQuery.Scope == QueryScope.Exact)
                    effectiveScope = QueryScope.Children;
            }
            var normalizedBasePath = effectivePath?.Trim('/') ?? "";

            // Track current result set for detecting changes
            var currentItems = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
            var disposables = new CompositeDisposable();

            // The Cosmos round-trip runs INSIDE the IIoPool — offloaded with
            // ConfigureAwait(false) throughout, so no hub/Orleans scheduler is
            // captured. Invoke (cold) keeps work-on-Subscribe semantics —
            // RunQuery is re-invoked per change batch as one fresh query.
            IObservable<List<(string? Path, T Item)>> RunQuery()
                => _ioPool.Invoke(ct => CollectQueryResultsAsync<T>(request, options, ct));

            // Race-fix (mirrors PostgreSqlMeshQuery / StorageAdapterMeshQueryProvider):
            // subscribe to the adapter's Changes BEFORE running the initial query so
            // notifications fired during the initial query's I/O window are captured
            // in a backlog instead of dropped (Changes is a plain Subject — no buffering).
            var earlyBacklog = new List<DataChangeNotification>();
            var earlyLock = new object();
            var initialDone = false;
            var earlySubscription = _adapter.Changes
                .Where(n => PathMatcher.ShouldNotify(n.Path, normalizedBasePath, effectiveScope))
                .Subscribe(n =>
                {
                    lock (earlyLock)
                    {
                        if (!initialDone)
                            earlyBacklog.Add(n);
                    }
                });
            disposables.Add(earlySubscription);

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

                    DataChangeNotification[] backlog;
                    // 1) Live pipeline first — starts buffering immediately. Strict
                    //    unit-of-work via Concat: one pooled RunQuery per change, so
                    //    the shared currentItems dictionary is never raced.
                    var changeBuffer = new Subject<DataChangeNotification>();
                    disposables.Add(changeBuffer);
                    disposables.Add(
                        _adapter.Changes
                            .Where(n => PathMatcher.ShouldNotify(n.Path, normalizedBasePath, effectiveScope))
                            .Subscribe(changeBuffer));
                    disposables.Add(
                        changeBuffer
                            .Select(n => RunQuery()
                                .Select(newResults => (batch: (IList<DataChangeNotification>)new[] { n }, newResults)))
                            .Concat()
                            .Subscribe(
                                t => ProcessBatch(t.batch, t.newResults, currentItems, parsedQuery, observer),
                                ex => observer.OnError(ex)));

                    // 2) Snapshot + clear the early backlog under lock.
                    lock (earlyLock)
                    {
                        backlog = earlyBacklog.ToArray();
                        earlyBacklog.Clear();
                        initialDone = true;
                    }
                    earlySubscription.Dispose();

                    observer.OnNext(new QueryResultChange<T>
                    {
                        ChangeType = QueryChangeType.Initial,
                        Items = initialItems,
                        Query = parsedQuery,
                        Version = Interlocked.Increment(ref _version),
                        Timestamp = DateTimeOffset.UtcNow
                    });

                    // 3) Drain the backlog through the same Concat-serialized
                    //    pipeline (a thread-pool tick avoids stack recursion through
                    //    the live Subscribe; skip once the consumer unsubscribed).
                    if (backlog.Length > 0)
                    {
                        disposables.Add(Scheduler.Default.Schedule(() =>
                        {
                            try
                            {
                                foreach (var n in backlog)
                                {
                                    if (disposables.IsDisposed)
                                        return;
                                    changeBuffer.OnNext(n);
                                }
                            }
                            catch (ObjectDisposedException)
                            {
                                // Subscription torn down mid-drain — consumer gone.
                            }
                        }));
                    }
                },
                ex => observer.OnError(ex)));

            return disposables;
        });
    }

    /// <summary>
    /// Persistence-layer async boundary: collects all results from
    /// <see cref="QueryAsync"/> into a list. Called exclusively from inside
    /// <see cref="IIoPool.Invoke{T}"/> (see <see cref="Query{T}"/>'s
    /// <c>RunQuery</c>) so the pump always runs behind the pool's gate on the
    /// ThreadPool — no hub/Orleans scheduler is ever captured.
    /// </summary>
    private async Task<List<(string? Path, T Item)>> CollectQueryResultsAsync<T>(
        MeshQueryRequest request, JsonSerializerOptions options, CancellationToken ct)
    {
        var results = new List<(string?, T)>();
        await foreach (var item in QueryAsync(request, options, ct).ConfigureAwait(false))
            if (item is T typed)
                results.Add(((item as MeshNode)?.Path, typed));
        return results;
    }

    private void ProcessBatch<T>(
        IList<DataChangeNotification> batch,
        List<(string? Path, T Item)> newResults,
        Dictionary<string, T> currentItems,
        ParsedQuery parsedQuery,
        IObserver<QueryResultChange<T>> observer)
    {
        var changesByPath = batch
            .GroupBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

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
            {
                if (changesByPath.ContainsKey(path))
                    updatedItems.Add(item);
            }
            else
            {
                addedItems.Add(item);
            }
        }

        foreach (var (path, item) in currentItems)
        {
            if (!newItems.ContainsKey(path))
                removedItems.Add(item);
        }

        currentItems.Clear();
        foreach (var (path, item) in newItems)
            currentItems[path] = item;

        void Emit(QueryChangeType type, IReadOnlyList<T> items)
        {
            if (items.Count == 0) return;
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
    /// Checks whether a node should be excluded based on context.
    /// </summary>
    private bool IsExcludedByContext(MeshNode node, string? context)
    {
        if (context == null) return false;
        if (_meshConfiguration?.IsExcludedFromContext(node.NodeType, context) == true)
            return true;
        if (node.IsExcludedFromContext(context))
            return true;
        return false;
    }

    /// <summary>
    /// Strips $type filter conditions from the parsed query AST.
    /// All items in the Cosmos nodes container are MeshNodes, so $type:MeshNode is redundant
    /// and any other $type value would match nothing.
    /// </summary>
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
}
