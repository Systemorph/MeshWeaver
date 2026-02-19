using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// PostgreSQL native implementation of IMeshQueryCore.
/// Translates parsed queries directly into PostgreSQL SQL via PostgreSqlStorageAdapter.
/// </summary>
public class PostgreSqlMeshQuery : IMeshQueryCore
{
    private readonly PostgreSqlStorageAdapter _adapter;
    private readonly IDataChangeNotifier? _changeNotifier;
    private readonly QueryParser _parser = new();
    private long _version;

    public static readonly TimeSpan DefaultDebounceInterval = TimeSpan.FromMilliseconds(100);

    public PostgreSqlMeshQuery(
        PostgreSqlStorageAdapter adapter,
        IDataChangeNotifier? changeNotifier = null)
    {
        _adapter = adapter;
        _changeNotifier = changeNotifier;
    }

    public async IAsyncEnumerable<object> QueryAsync(
        MeshQueryRequest request,
        JsonSerializerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
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

        // When ContextPath is set, buffer results to apply proximity re-ranking
        if (!string.IsNullOrEmpty(request.ContextPath))
        {
            var buffered = new List<(MeshNode Node, double Score)>();
            await foreach (var node in _adapter.QueryNodesAsync(parsedQuery, options, request.UserId, ct: ct))
            {
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

        await foreach (var node in _adapter.QueryNodesAsync(parsedQuery, options, request.UserId, ct: ct))
        {
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

    public IAsyncEnumerable<QuerySuggestion> AutocompleteAsync(
        string basePath,
        string prefix,
        JsonSerializerOptions options,
        int limit = 10,
        CancellationToken ct = default)
        => AutocompleteAsync(basePath, prefix, options, AutocompleteMode.PathFirst, limit, null, ct);

    public async IAsyncEnumerable<QuerySuggestion> AutocompleteAsync(
        string basePath,
        string prefix,
        JsonSerializerOptions options,
        AutocompleteMode mode,
        int limit = 10,
        string? contextPath = null,
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

        await foreach (var node in _adapter.QueryNodesAsync(query, options, ct: ct))
        {
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

        await foreach (var node in _adapter.QueryNodesAsync(query, options, ct: ct))
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
        return Observable.Create<QueryResultChange<T>>(async (observer, ct) =>
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

            var currentItems = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var initialItems = new List<T>();
                await foreach (var item in QueryAsync(request, options, ct))
                {
                    if (item is T typedItem)
                    {
                        initialItems.Add(typedItem);
                        var itemPath = (item as MeshNode)?.Path;
                        if (!string.IsNullOrEmpty(itemPath))
                            currentItems[itemPath] = typedItem;
                    }
                }

                observer.OnNext(new QueryResultChange<T>
                {
                    ChangeType = QueryChangeType.Initial,
                    Items = initialItems,
                    Query = parsedQuery,
                    Version = Interlocked.Increment(ref _version),
                    Timestamp = DateTimeOffset.UtcNow
                });
            }
            catch (Exception ex)
            {
                observer.OnError(ex);
                return Disposable.Empty;
            }

            if (_changeNotifier == null)
            {
                observer.OnCompleted();
                return Disposable.Empty;
            }

            var changeBuffer = new Subject<DataChangeNotification>();
            var subscription = new CompositeDisposable();

            var notifierSubscription = _changeNotifier
                .Where(n => PathMatcher.ShouldNotify(n.Path, normalizedBasePath, effectiveScope))
                .Subscribe(changeBuffer);
            subscription.Add(notifierSubscription);

            var debounceSubscription = changeBuffer
                .Buffer(DefaultDebounceInterval)
                .Where(batch => batch.Count > 0)
                .Subscribe(async batch =>
                {
                    try
                    {
                        await ProcessChangeBatchAsync(batch, request, options, parsedQuery, currentItems, observer, ct);
                    }
                    catch (Exception ex)
                    {
                        observer.OnError(ex);
                    }
                });
            subscription.Add(debounceSubscription);
            subscription.Add(changeBuffer);

            return subscription;
        });
    }

    private async Task ProcessChangeBatchAsync<T>(
        IList<DataChangeNotification> batch,
        MeshQueryRequest request,
        JsonSerializerOptions options,
        ParsedQuery parsedQuery,
        Dictionary<string, T> currentItems,
        IObserver<QueryResultChange<T>> observer,
        CancellationToken ct)
    {
        var changesByPath = batch
            .GroupBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

        var newItems = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        await foreach (var item in QueryAsync(request, options, ct))
        {
            if (item is T typedItem)
            {
                var itemPath = (item as MeshNode)?.Path;
                if (!string.IsNullOrEmpty(itemPath))
                    newItems[itemPath] = typedItem;
            }
        }

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

        if (addedItems.Count > 0)
        {
            observer.OnNext(new QueryResultChange<T>
            {
                ChangeType = QueryChangeType.Added,
                Items = addedItems,
                Query = parsedQuery,
                Version = Interlocked.Increment(ref _version),
                Timestamp = DateTimeOffset.UtcNow
            });
        }

        if (updatedItems.Count > 0)
        {
            observer.OnNext(new QueryResultChange<T>
            {
                ChangeType = QueryChangeType.Updated,
                Items = updatedItems,
                Query = parsedQuery,
                Version = Interlocked.Increment(ref _version),
                Timestamp = DateTimeOffset.UtcNow
            });
        }

        if (removedItems.Count > 0)
        {
            observer.OnNext(new QueryResultChange<T>
            {
                ChangeType = QueryChangeType.Removed,
                Items = removedItems,
                Query = parsedQuery,
                Version = Interlocked.Increment(ref _version),
                Timestamp = DateTimeOffset.UtcNow
            });
        }
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
}
