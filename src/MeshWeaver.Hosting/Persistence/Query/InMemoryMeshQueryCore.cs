using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Persistence.Query;

/// <summary>
/// Unsecured in-memory implementation of <see cref="IMeshQueryCore"/>.
///
/// <para>This is the FULL parsing / matching / sorting / paging / change-stream
/// machinery, with NO ISecurityService dependency. Consumers that need an
/// access-controlled surface use <see cref="InMemoryMeshQuery"/> (which
/// inherits from this class and layers per-node read filtering on top).</para>
///
/// <para>Decoupling rationale: SecurityService consumes a synced query (via
/// <c>SyncedQueryMeshNodes</c>) for its own AccessAssignment lookup. If the
/// synced query resolves a provider that depends on ISecurityService at
/// construction time, Autofac sees a cycle:
/// SecurityService → workspace.GetQuery → SyncedQueryMeshNodes →
/// IMeshQueryCore (resolves InMemoryMeshQuery) → ISecurityService.
/// Splitting the surface so <see cref="IMeshQueryCore"/> resolves to a class
/// with NO ISecurityService parameter breaks the cycle structurally.</para>
/// </summary>
internal class InMemoryMeshQueryCore(
    IStorageService persistence,
    AccessService? accessService = null,
    IDataChangeNotifier? changeNotifier = null,
    MeshConfiguration? meshConfiguration = null,
    ILogger<InMemoryMeshQueryCore>? logger = null)
    : IMeshQueryCore
{
    protected readonly IStorageService Persistence = persistence;
    protected readonly AccessService? AccessServiceField = accessService;
    protected readonly IDataChangeNotifier? ChangeNotifier = changeNotifier;
    protected readonly MeshConfiguration? MeshConfigField = meshConfiguration;
    protected readonly ILogger? Logger = logger;
    protected readonly QueryParser Parser = new();
    protected readonly QueryEvaluator Evaluator = new();
    private long _version;

    public static readonly TimeSpan DefaultDebounceInterval = TimeSpan.FromMilliseconds(100);

    /// <inheritdoc />
    public async IAsyncEnumerable<object> QueryAsync(
        MeshQueryRequest request,
        JsonSerializerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var item in QueryCoreAsync(request, options, ct))
            yield return item;
    }

    /// <inheritdoc />
    public IObservable<QueryResultChange<T>> ObserveQuery<T>(
        MeshQueryRequest request, JsonSerializerOptions options)
        => ObserveQueryInternal<T>(request, options, useSecurityFilter: false);

    /// <summary>
    /// Effective user id from request or AccessService; defaults to Anonymous.
    /// </summary>
    protected string GetEffectiveUserId(MeshQueryRequest request)
    {
        if (request.UserId != null)
            return string.IsNullOrEmpty(request.UserId) ? WellKnownUsers.Anonymous : request.UserId;
        var userId = AccessServiceField?.Context?.ObjectId
                     ?? AccessServiceField?.CircuitContext?.ObjectId;
        return string.IsNullOrEmpty(userId) ? WellKnownUsers.Anonymous : userId;
    }

    /// <summary>
    /// Unsecured query: parses, finds matches, sorts, pages. No access-control
    /// filtering. Used directly by <see cref="IMeshQueryCore"/> consumers and
    /// shared with <see cref="InMemoryMeshQuery"/>'s secured pipeline.
    /// </summary>
    protected async IAsyncEnumerable<object> QueryCoreAsync(
        MeshQueryRequest request,
        JsonSerializerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var parsedQuery = Parser.Parse(request.Query);
        if (request.Limit.HasValue)
            parsedQuery = parsedQuery with { Limit = request.Limit };
        if (parsedQuery.Source is QuerySource.Activity or QuerySource.Accessed)
            parsedQuery = parsedQuery with { IsMain = true };

        var effectivePath = parsedQuery.Path;
        var effectiveScope = parsedQuery.Scope;
        if (string.IsNullOrEmpty(effectivePath))
        {
            if (!string.IsNullOrEmpty(request.DefaultPath))
                effectivePath = request.DefaultPath;
            if (parsedQuery.Scope == QueryScope.Exact)
                effectiveScope = parsedQuery.HasConditions ? QueryScope.Subtree : QueryScope.Children;
        }

        var basePath = NormalizePath(effectivePath);
        var userId = GetEffectiveUserId(request);
        var context = request.Context ?? parsedQuery.Context;

        var matched = new List<object>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);

        await foreach (var node in FindMatchingNodesAsync(
            parsedQuery, effectiveScope, basePath, userId, context, request, options, ct))
        {
            if (seen.Add(node))
                matched.Add(node);
        }

        if (parsedQuery.Source == QuerySource.Activity && matched.Count > 0)
        {
            var activityMainPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await foreach (var actNode in Persistence.GetAllDescendantsAsync(basePath, options)
                .WithCancellation(ct))
            {
                if (actNode.NodeType == ActivityNodeType.NodeType
                    && !string.IsNullOrEmpty(actNode.MainNode))
                    activityMainPaths.Add(actNode.MainNode);
            }
            matched = matched
                .Where(n => n is MeshNode mn && activityMainPaths.Contains(mn.Path ?? ""))
                .ToList();
        }

        IEnumerable<object> sorted = matched;
        if (parsedQuery.OrderBy != null)
        {
            sorted = parsedQuery.OrderBy.Descending
                ? matched.OrderByDescending(n => GetSortableValue(n, parsedQuery.OrderBy.Property))
                : matched.OrderBy(n => GetSortableValue(n, parsedQuery.OrderBy.Property));
        }

        int skipped = 0, yielded = 0;
        foreach (var node in sorted)
        {
            if (request.Skip.HasValue && request.Skip.Value > 0 && skipped < request.Skip.Value)
            { skipped++; continue; }

            yield return parsedQuery.Select != null
                ? ParsedQuery.ProjectToSelect(node, parsedQuery.Select)
                : node;

            yielded++;
            if (parsedQuery.Limit.HasValue && parsedQuery.Limit.Value > 0
                && yielded >= parsedQuery.Limit.Value)
                yield break;
        }
    }

    /// <summary>
    /// Hook for subclasses to provide the SECURED query stream used inside
    /// <see cref="ObserveQueryInternal{T}"/> when <c>useSecurityFilter</c>
    /// is requested. Default returns the unsecured stream (no filter).
    /// <see cref="InMemoryMeshQuery"/> overrides to apply ISecurityService.
    /// </summary>
    protected virtual IAsyncEnumerable<object> GetSecuredQueryStream(
        MeshQueryRequest request, JsonSerializerOptions options, CancellationToken ct)
        => QueryCoreAsync(request, options, ct);

    protected async IAsyncEnumerable<object> FindMatchingNodesAsync(
        ParsedQuery parsedQuery,
        QueryScope effectiveScope,
        string basePath,
        string userId,
        string? context,
        MeshQueryRequest request,
        JsonSerializerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var pathsToSearch = GetPathsForScope(basePath, effectiveScope);

        foreach (var searchPath in pathsToSearch)
        {
            var node = await Persistence.GetNodeSecure(searchPath, userId, options).FirstAsync().ToTask(ct);
            if (node == null) continue;
            if (IsExcludedByContext(node, context)) continue;
            if (IsExcludedByIsMain(node, parsedQuery)) continue;
            if (Evaluator.Matches(node, parsedQuery))
                yield return node;
        }

        // Subtree / Children: enumerate descendants and filter
        if (effectiveScope is QueryScope.Subtree or QueryScope.Children)
        {
            await foreach (var descendant in Persistence.GetAllDescendantsAsync(basePath, options)
                .WithCancellation(ct))
            {
                if (descendant.Path == basePath) continue;
                if (effectiveScope == QueryScope.Children)
                {
                    var rel = descendant.Path?[basePath.Length..]?.TrimStart('/') ?? "";
                    if (rel.Contains('/')) continue;
                }
                if (IsExcludedByContext(descendant, context)) continue;
                if (IsExcludedByIsMain(descendant, parsedQuery)) continue;
                if (Evaluator.Matches(descendant, parsedQuery))
                    yield return descendant;
            }
        }
    }

    protected static string? GetItemPath(object item) => item switch
    {
        MeshNode n => n.Path,
        _ => null,
    };

    protected static object? GetSortableValue(object item, string property)
    {
        if (item is MeshNode node)
        {
            return property.ToLowerInvariant() switch
            {
                "name" => node.Name,
                "path" => node.Path,
                "createddate" or "created" => node.CreatedDate,
                "lastmodified" or "modified" => node.LastModified,
                "version" => node.Version,
                _ => null,
            };
        }
        return null;
    }

    protected static string NormalizePath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        return path.Trim('/');
    }

    protected static List<string> GetPathsForScope(string basePath, QueryScope scope)
    {
        if (scope == QueryScope.Exact) return new List<string> { basePath };
        // Subtree / Children: only return basePath; descendants are streamed in FindMatchingNodesAsync.
        return new List<string> { basePath };
    }

    protected bool IsExcludedByContext(MeshNode node, string? context)
    {
        if (context == null) return false;
        if (MeshConfigField?.IsExcludedFromContext(node.NodeType, context) == true)
            return true;
        if (node.ExcludeFromContext?.Contains(context) == true)
            return true;
        return false;
    }

    protected static bool IsExcludedByIsMain(MeshNode node, ParsedQuery query)
    {
        if (query.IsMain != true) return false;
        return !string.IsNullOrEmpty(node.MainNode) && node.MainNode != node.Path;
    }

    protected IObservable<QueryResultChange<T>> ObserveQueryInternal<T>(
        MeshQueryRequest request,
        JsonSerializerOptions options,
        bool useSecurityFilter)
    {
        return Observable.Create<QueryResultChange<T>>(async (observer, ct) =>
        {
            var parsedQuery = Parser.Parse(request.Query);
            var effectivePath = parsedQuery.Path;
            var effectiveScope = parsedQuery.Scope;
            if (string.IsNullOrEmpty(effectivePath))
            {
                effectivePath = request.DefaultPath ?? "";
                if (parsedQuery.Scope == QueryScope.Exact)
                    effectiveScope = parsedQuery.HasConditions ? QueryScope.Subtree : QueryScope.Children;
            }
            var normalizedBasePath = NormalizePath(effectivePath);
            var currentItems = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);

            IAsyncEnumerable<object> QueryStream(CancellationToken token) =>
                useSecurityFilter
                    ? GetSecuredQueryStream(request, options, token)
                    : QueryCoreAsync(request, options, token);

            try
            {
                var initialItems = new List<T>();
                await foreach (var item in QueryStream(ct))
                {
                    if (item is T typed)
                    {
                        initialItems.Add(typed);
                        var p = GetItemPath(item);
                        if (!string.IsNullOrEmpty(p))
                            currentItems[p] = typed;
                    }
                }
                observer.OnNext(new QueryResultChange<T>
                {
                    ChangeType = QueryChangeType.Initial,
                    Items = initialItems,
                    Query = parsedQuery,
                    Version = Interlocked.Increment(ref _version),
                    Timestamp = DateTimeOffset.UtcNow,
                });
            }
            catch (Exception ex)
            {
                observer.OnError(ex);
                return Disposable.Empty;
            }

            if (ChangeNotifier == null)
            {
                observer.OnCompleted();
                return Disposable.Empty;
            }

            var changeBuffer = new Subject<DataChangeNotification>();
            var subscription = new CompositeDisposable();

            var notifierSubscription = ChangeNotifier
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
                        await ProcessChangeBatchAsync(batch, request, options, parsedQuery, currentItems, observer, ct, useSecurityFilter);
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
        CancellationToken ct,
        bool useSecurityFilter)
    {
        var addedItems = new List<T>();
        var updatedItems = new List<T>();
        var removedItems = new List<T>();

        var changesByPath = batch
            .GroupBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

        var newItems = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        var queryStream = useSecurityFilter
            ? GetSecuredQueryStream(request, options, ct)
            : QueryCoreAsync(request, options, ct);
        await foreach (var item in queryStream)
        {
            if (item is T typed)
            {
                var p = GetItemPath(item);
                if (!string.IsNullOrEmpty(p))
                    newItems[p] = typed;
            }
        }

        foreach (var (path, change) in changesByPath)
        {
            if (change.Entity is T directMatch && Evaluator.Matches(directMatch, parsedQuery))
            {
                if (change.Kind == DataChangeKind.Deleted)
                    newItems.Remove(path);
                else
                    newItems[path] = directMatch;
            }
        }

        foreach (var (path, item) in newItems)
        {
            if (currentItems.TryGetValue(path, out var existing))
            {
                if (!ItemEquals(existing, item))
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
        foreach (var (p, v) in newItems) currentItems[p] = v;

        void Emit(QueryChangeType type, IReadOnlyList<T> items)
        {
            if (items.Count == 0) return;
            observer.OnNext(new QueryResultChange<T>
            {
                ChangeType = type,
                Items = items,
                Query = parsedQuery,
                Version = Interlocked.Increment(ref _version),
                Timestamp = DateTimeOffset.UtcNow,
            });
        }
        Emit(QueryChangeType.Added, addedItems);
        Emit(QueryChangeType.Updated, updatedItems);
        Emit(QueryChangeType.Removed, removedItems);
    }

    private static bool ItemEquals<T>(T a, T b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        if (a is MeshNode na && b is MeshNode nb)
            return na.Version == nb.Version && na.Name == nb.Name && Equals(na.Content, nb.Content);
        return EqualityComparer<T>.Default.Equals(a, b);
    }
}
