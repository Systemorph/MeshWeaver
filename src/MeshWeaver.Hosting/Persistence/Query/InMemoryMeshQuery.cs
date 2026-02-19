using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

namespace MeshWeaver.Hosting.Persistence.Query;

/// <summary>
/// In-memory implementation of IMeshQuery.
/// Extracts query functionality from InMemoryPersistenceService for use as a standalone service.
/// </summary>
public class InMemoryMeshQuery : IMeshQueryCore
{
    private readonly IPersistenceServiceCore _persistence;
    private readonly ISecurityService? _securityService;
    private readonly AccessService? _accessService;
    private readonly IDataChangeNotifier? _changeNotifier;
    private readonly QueryParser _parser = new();
    private readonly QueryEvaluator _evaluator = new();
    private long _version;

    /// <summary>
    /// Default debounce interval for batching rapid changes.
    /// </summary>
    public static readonly TimeSpan DefaultDebounceInterval = TimeSpan.FromMilliseconds(100);

    public InMemoryMeshQuery(
        IPersistenceServiceCore persistence,
        ISecurityService? securityService = null,
        AccessService? accessService = null,
        IDataChangeNotifier? changeNotifier = null)
    {
        _persistence = persistence;
        _securityService = securityService;
        _accessService = accessService;
        _changeNotifier = changeNotifier;
    }

    /// <summary>
    /// Gets the effective user ID from the request or from the current access context.
    /// Returns WellKnownUsers.Public for anonymous/unauthenticated access.
    /// </summary>
    private string GetEffectiveUserId(MeshQueryRequest request)
    {
        // If request has explicit UserId, use it
        if (!string.IsNullOrEmpty(request.UserId))
            return request.UserId;

        // Get from access context, defaulting to Public for anonymous users
        var userId = _accessService?.Context?.ObjectId;
        return string.IsNullOrEmpty(userId) ? WellKnownUsers.Public : userId;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<object> QueryAsync(
        MeshQueryRequest request,
        JsonSerializerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var parsedQuery = _parser.Parse(request.Query);

        // Override limit from request if provided
        if (request.Limit.HasValue)
        {
            parsedQuery = parsedQuery with { Limit = request.Limit };
        }

        // If no path is specified, use the default path from request or default to root
        var effectivePath = parsedQuery.Path;
        var effectiveScope = parsedQuery.Scope;
        if (string.IsNullOrEmpty(effectivePath))
        {
            if (!string.IsNullOrEmpty(request.DefaultPath))
            {
                effectivePath = request.DefaultPath;
            }
            // When no path specified and scope is Exact, default to Children (items in current namespace only)
            // This ensures queries like "nodeType:Organization" find root-level organizations
            // Use scope:descendants explicitly for recursive search
            if (parsedQuery.Scope == QueryScope.Exact)
            {
                effectiveScope = QueryScope.Children;
            }
        }

        var basePath = NormalizePath(effectivePath);

        // Determine paths to search based on scope
        var pathsToSearch = GetPathsForScope(basePath, effectiveScope);

        // Collect results with fuzzy scores for ordering
        var results = new List<(object Item, int Score)>();

        // Get the effective user ID for security filtering (from request or access context)
        var userId = GetEffectiveUserId(request);

        foreach (var searchPath in pathsToSearch)
        {
            // Search MeshNodes at this path (with security filtering)
            var node = await _persistence.GetNodeSecureAsync(searchPath, userId, options, ct);
            if (node != null)
            {
                if (_evaluator.Matches(node, parsedQuery))
                {
                    var score = _evaluator.GetFuzzyScore(node, parsedQuery.TextSearch);
                    score += (int)PathProximity.ComputeBoost(request.ContextPath, node.Path);
                    results.Add((node, score));
                }
            }

        }

        // If we're doing scope=children, search immediate children only
        if (effectiveScope == QueryScope.Children)
        {
            await foreach (var child in _persistence.GetChildrenSecureAsync(basePath, userId, options).WithCancellation(ct))
            {
                // Evaluate the node itself
                if (_evaluator.Matches(child, parsedQuery))
                {
                    var score = _evaluator.GetFuzzyScore(child, parsedQuery.TextSearch);
                    score += (int)PathProximity.ComputeBoost(request.ContextPath, child.Path);
                    // Avoid duplicates
                    if (!results.Any(r => ReferenceEquals(r.Item, child)))
                        results.Add((child, score));
                }

            }
        }

        // If scope includes ancestors (AncestorsAndSelf, Hierarchy), also search children of self and each ancestor path
        // This allows finding agents like "TodoAgent" (child of self) and "Navigator" (child of root)
        // when searching from "ACME/Project"
        if (effectiveScope == QueryScope.AncestorsAndSelf || effectiveScope == QueryScope.Hierarchy || effectiveScope == QueryScope.Ancestors)
        {
            // Get self + ancestors for AncestorsAndSelf, just ancestors for Ancestors
            var pathsToSearchChildren = effectiveScope == QueryScope.Ancestors
                ? GetPathsForScope(basePath, QueryScope.Ancestors)
                : GetPathsForScope(basePath, QueryScope.AncestorsAndSelf);

            foreach (var ancestorPath in pathsToSearchChildren)
            {
                await foreach (var child in _persistence.GetChildrenSecureAsync(ancestorPath, userId, options).WithCancellation(ct))
                {
                    // Evaluate the node itself
                    if (_evaluator.Matches(child, parsedQuery))
                    {
                        var score = _evaluator.GetFuzzyScore(child, parsedQuery.TextSearch);
                        score += (int)PathProximity.ComputeBoost(request.ContextPath, child.Path);
                        // Avoid duplicates
                        if (!results.Any(r => ReferenceEquals(r.Item, child)))
                            results.Add((child, score));
                    }
                }
            }
        }

        // If we're doing scope=descendants, also search descendant paths recursively
        if (effectiveScope == QueryScope.Descendants || effectiveScope == QueryScope.Hierarchy || effectiveScope == QueryScope.Subtree)
        {
            await foreach (var descendant in _persistence.GetDescendantsSecureAsync(basePath, userId, options).WithCancellation(ct))
            {
                // Evaluate the node itself
                if (_evaluator.Matches(descendant, parsedQuery))
                {
                    var score = _evaluator.GetFuzzyScore(descendant, parsedQuery.TextSearch);
                    score += (int)PathProximity.ComputeBoost(request.ContextPath, descendant.Path);
                    // Avoid duplicates
                    if (!results.Any(r => ReferenceEquals(r.Item, descendant)))
                        results.Add((descendant, score));
                }

            }
        }

        // Order by fuzzy score (higher first) for text searches or when proximity boost is active
        IEnumerable<object> orderedResults;
        if (!string.IsNullOrEmpty(parsedQuery.TextSearch) || !string.IsNullOrEmpty(request.ContextPath))
        {
            orderedResults = results.OrderByDescending(r => r.Score).Select(r => r.Item);
        }
        else if (parsedQuery.OrderBy != null)
        {
            orderedResults = _evaluator.OrderResults(results.Select(r => r.Item), parsedQuery.OrderBy);
        }
        else
        {
            orderedResults = results.Select(r => r.Item);
        }

        // Apply skip (for paging)
        if (request.Skip.HasValue && request.Skip.Value > 0)
        {
            orderedResults = orderedResults.Skip(request.Skip.Value);
        }

        // Apply limit
        if (parsedQuery.Limit.HasValue && parsedQuery.Limit.Value > 0)
        {
            orderedResults = _evaluator.LimitResults(orderedResults, parsedQuery.Limit);
        }

        foreach (var item in orderedResults)
        {
            // Apply access control filtering if security service is available
            if (_securityService != null)
            {
                var itemPath = GetItemPath(item);
                if (!string.IsNullOrEmpty(itemPath))
                {
                    var permissions = await _securityService.GetEffectivePermissionsAsync(
                        itemPath, userId, ct);
                    if (!permissions.HasFlag(Permission.Read))
                        continue; // Skip items user cannot read
                }
            }

            yield return item;
        }
    }

    /// <summary>
    /// Gets the path for an item (MeshNode or object with Path property).
    /// </summary>
    private static string? GetItemPath(object item)
    {
        if (item is MeshNode node)
            return node.Path;

        // Try to get Path property via reflection for partition objects
        var pathProp = item.GetType().GetProperty("Path");
        if (pathProp != null)
            return pathProp.GetValue(item) as string;

        return null;
    }

    /// <inheritdoc />
    public IAsyncEnumerable<QuerySuggestion> AutocompleteAsync(
        string basePath,
        string prefix,
        JsonSerializerOptions options,
        int limit = 10,
        CancellationToken ct = default)
        => AutocompleteAsync(basePath, prefix, options, null, AutocompleteMode.PathFirst, limit, null, ct);

    /// <inheritdoc />
    public IAsyncEnumerable<QuerySuggestion> AutocompleteAsync(
        string basePath,
        string prefix,
        JsonSerializerOptions options,
        AutocompleteMode mode,
        int limit = 10,
        string? contextPath = null,
        CancellationToken ct = default)
        => AutocompleteAsync(basePath, prefix, options, null, mode, limit, contextPath, ct);

    /// <summary>
    /// Autocomplete with user ID for access control filtering.
    /// </summary>
    public async IAsyncEnumerable<QuerySuggestion> AutocompleteAsync(
        string basePath,
        string prefix,
        JsonSerializerOptions options,
        string? userId,
        AutocompleteMode mode,
        int limit = 10,
        string? contextPath = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(basePath);
        var normalizedPrefix = prefix ?? "";

        var suggestions = new List<QuerySuggestion>();

        // Search descendants for matching nodes (with security filtering)
        await foreach (var node in _persistence.GetDescendantsSecureAsync(normalizedPath, userId, options).WithCancellation(ct))
        {
            var name = node.Name ?? node.Id ?? node.Path ?? "";
            var nameLower = name;
            var pathLower = node.Path ?? "";

            // Calculate match score based on prefix match (check both name and path)
            double score = 0;

            // Name matches (higher priority)
            if (nameLower.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                score = 100 - (nameLower.Length - normalizedPrefix.Length); // Exact prefix match, shorter is better
            }
            else if (nameLower.Contains(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                score = 50 - (nameLower.IndexOf(normalizedPrefix, StringComparison.OrdinalIgnoreCase)); // Contains match
            }
            // Path matches (lower priority than name)
            else if (pathLower.Contains(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                score = 30 - (pathLower.IndexOf(normalizedPrefix, StringComparison.OrdinalIgnoreCase) * 0.1); // Path contains match
            }
            else if (FuzzyMatch(nameLower, normalizedPrefix))
            {
                score = 25; // Fuzzy match on name
            }

            score += PathProximity.ComputeBoost(contextPath, node.Path);

            if (score > 0)
            {
                suggestions.Add(new QuerySuggestion(node.Path ?? "", name, node.NodeType, score, node.Icon));
            }
        }

        // Order based on mode
        IEnumerable<QuerySuggestion> ordered = mode switch
        {
            // PathFirst: path length first, then score, then name (for path-based autocomplete like @references)
            AutocompleteMode.PathFirst => suggestions
                .OrderBy(s => s.Path.Length)
                .ThenByDescending(s => s.Score)
                .ThenBy(s => s.Name),

            // RelevanceFirst: score first (name match > path match > other), then path length, then name (for node selection)
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

    private static bool FuzzyMatch(string text, string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
            return true;

        int prefixIndex = 0;
        foreach (var c in text)
        {
            if (char.ToLowerInvariant(c) == char.ToLowerInvariant(prefix[prefixIndex]))
            {
                prefixIndex++;
                if (prefixIndex >= prefix.Length)
                    return true;
            }
        }
        return false;
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return "";

        // Remove leading/trailing slashes and normalize
        return path.Trim('/').Replace('\\', '/');
    }

    private static List<string> GetPathsForScope(string basePath, QueryScope scope)
    {
        var paths = new List<string>();

        // Children and Descendants scopes do NOT include self
        // Children are fetched separately by GetChildrenAsync
        // Descendants are fetched separately by GetDescendantsAsync
        if (scope == QueryScope.Children || scope == QueryScope.Descendants)
        {
            return paths;
        }

        // Include self for: Exact, Hierarchy, Subtree, AncestorsAndSelf
        // Ancestors does NOT include self
        if (scope != QueryScope.Ancestors)
        {
            paths.Add(basePath);
        }

        // Add ancestor paths for: Ancestors, AncestorsAndSelf, Hierarchy
        if (scope == QueryScope.Ancestors || scope == QueryScope.AncestorsAndSelf || scope == QueryScope.Hierarchy)
        {
            var segments = basePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int i = segments.Length - 1; i >= 0; i--)
            {
                var ancestorPath = string.Join("/", segments.Take(i));
                if (!paths.Contains(ancestorPath, StringComparer.OrdinalIgnoreCase))
                    paths.Add(ancestorPath);
            }
        }

        return paths;
    }

    /// <inheritdoc />
    public async Task<T?> SelectAsync<T>(string path, string property, JsonSerializerOptions options, CancellationToken ct = default)
    {
        var node = await _persistence.GetNodeAsync(path, options, ct);
        if (node == null)
            return default;

        var prop = typeof(MeshNode).GetProperty(property);
        if (prop == null)
            return default;

        var value = prop.GetValue(node);
        if (value is T typedValue)
            return typedValue;

        return default;
    }

    /// <inheritdoc />
    public IObservable<QueryResultChange<T>> ObserveQuery<T>(MeshQueryRequest request, JsonSerializerOptions options)
    {
        return Observable.Create<QueryResultChange<T>>(async (observer, ct) =>
        {
            var parsedQuery = _parser.Parse(request.Query);

            // Determine the effective path and scope
            var effectivePath = parsedQuery.Path;
            var effectiveScope = parsedQuery.Scope;
            if (string.IsNullOrEmpty(effectivePath))
            {
                effectivePath = request.DefaultPath ?? "";
                if (parsedQuery.Scope == QueryScope.Exact)
                {
                    effectiveScope = QueryScope.Children;
                }
            }
            var normalizedBasePath = NormalizePath(effectivePath);

            // Track current result set for detecting changes
            var currentItems = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);

            // Emit initial results
            try
            {
                var initialItems = new List<T>();
                await foreach (var item in QueryAsync(request, options, ct))
                {
                    if (item is T typedItem)
                    {
                        initialItems.Add(typedItem);
                        var itemPath = GetItemPath(item);
                        if (!string.IsNullOrEmpty(itemPath))
                            currentItems[itemPath] = typedItem;
                    }
                }

                var initialChange = new QueryResultChange<T>
                {
                    ChangeType = QueryChangeType.Initial,
                    Items = initialItems,
                    Query = parsedQuery,
                    Version = Interlocked.Increment(ref _version),
                    Timestamp = DateTimeOffset.UtcNow
                };
                observer.OnNext(initialChange);
            }
            catch (Exception ex)
            {
                observer.OnError(ex);
                return Disposable.Empty;
            }

            // If no change notifier is available, complete after initial results
            if (_changeNotifier == null)
            {
                observer.OnCompleted();
                return Disposable.Empty;
            }

            // Subscribe to changes with debouncing
            var changeBuffer = new Subject<DataChangeNotification>();
            var subscription = new CompositeDisposable();

            // Subscribe to the change notifier and filter by path/scope
            var notifierSubscription = _changeNotifier
                .Where(n => PathMatcher.ShouldNotify(n.Path, normalizedBasePath, effectiveScope))
                .Subscribe(changeBuffer);
            subscription.Add(notifierSubscription);

            // Process debounced changes
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
        var addedItems = new List<T>();
        var updatedItems = new List<T>();
        var removedItems = new List<T>();

        // Group changes by path to handle multiple changes to the same item
        var changesByPath = batch
            .GroupBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

        // Re-query to get current matching items
        var newItems = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        await foreach (var item in QueryAsync(request, options, ct))
        {
            if (item is T typedItem)
            {
                var itemPath = GetItemPath(item);
                if (!string.IsNullOrEmpty(itemPath))
                    newItems[itemPath] = typedItem;
            }
        }

        // Detect added and updated items
        foreach (var (path, item) in newItems)
        {
            if (currentItems.TryGetValue(path, out var existingItem))
            {
                // Item existed before - check if it changed
                if (changesByPath.ContainsKey(path))
                {
                    updatedItems.Add(item);
                }
            }
            else
            {
                // New item
                addedItems.Add(item);
            }
        }

        // Detect removed items
        foreach (var (path, item) in currentItems)
        {
            if (!newItems.ContainsKey(path))
            {
                removedItems.Add(item);
            }
        }

        // Update current items
        currentItems.Clear();
        foreach (var (path, item) in newItems)
        {
            currentItems[path] = item;
        }

        // Emit changes
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
}
