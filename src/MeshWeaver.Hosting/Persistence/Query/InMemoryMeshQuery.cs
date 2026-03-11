using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Persistence.Query;

/// <summary>
/// In-memory implementation of IMeshService.
/// Extracts query functionality from InMemoryPersistenceService for use as a standalone service.
/// </summary>
internal class InMemoryMeshQuery(
    IStorageService persistence,
    ISecurityService? securityService = null,
    AccessService? accessService = null,
    IDataChangeNotifier? changeNotifier = null,
    MeshConfiguration? meshConfiguration = null,
    IEnumerable<INodeValidator>? nodeValidators = null,
    ILogger<InMemoryMeshQuery>? logger = null)
    : IMeshQueryProvider
{
    private readonly QueryParser _parser = new();
    private readonly QueryEvaluator _evaluator = new();
    private long _version;

    /// <summary>
    /// Default debounce interval for batching rapid changes.
    /// </summary>
    public static readonly TimeSpan DefaultDebounceInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Gets the effective user ID from the request or from the current access context.
    /// Returns WellKnownUsers.Anonymous for unauthenticated/virtual access.
    /// </summary>
    private string GetEffectiveUserId(MeshQueryRequest request)
    {
        // If request has explicit UserId, use it
        if (!string.IsNullOrEmpty(request.UserId))
            return request.UserId;

        // Get from access context, defaulting to Anonymous for unauthenticated users
        var userId = accessService?.Context?.ObjectId;
        return string.IsNullOrEmpty(userId) ? WellKnownUsers.Anonymous : userId;
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

        // Both source:activity and source:accessed restrict to main nodes only (main_node = path).
        // In-memory doesn't support the JOIN — returns main nodes without activity ordering.
        if (parsedQuery.Source is QuerySource.Activity or QuerySource.Accessed)
        {
            parsedQuery = parsedQuery with { IsMain = true };
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

        // Get the effective user ID for security filtering (from request or access context)
        var userId = GetEffectiveUserId(request);

        // Context-based exclusion
        var context = request.Context ?? parsedQuery.Context;

        // Stream results immediately as they are found — no buffering.
        // Skip/limit are applied as inline counters.
        int skipped = 0;
        int yielded = 0;
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);

        await foreach (var node in FindMatchingNodesAsync(
            parsedQuery, effectiveScope, basePath, userId, context, request, options, ct))
        {
            if (!seen.Add(node))
                continue;

            // Apply skip
            if (request.Skip.HasValue && request.Skip.Value > 0 && skipped < request.Skip.Value)
            {
                skipped++;
                continue;
            }

            // Apply access control filtering.
            // For MeshNode items, rely solely on INodeValidator (which handles custom access
            // rules, self-access, and standard RLS). For non-MeshNode items (partition objects),
            // use inline permission check since validators only apply to MeshNodes.
            if (node is MeshNode meshNode)
            {
                if (!await ValidateReadAsync(meshNode, userId, ct))
                    continue;
            }
            else if (securityService != null)
            {
                var itemPath = GetItemPath(node);
                if (!string.IsNullOrEmpty(itemPath))
                {
                    var permissions = await securityService.GetEffectivePermissionsAsync(
                        itemPath, userId, ct);
                    if (!permissions.HasFlag(Permission.Read))
                        continue;
                }
            }

            yield return parsedQuery.Select != null
                ? ParsedQuery.ProjectToSelect(node, parsedQuery.Select)
                : node;

            // Apply limit
            yielded++;
            if (parsedQuery.Limit.HasValue && parsedQuery.Limit.Value > 0
                && yielded >= parsedQuery.Limit.Value)
                yield break;
        }
    }

    /// <summary>
    /// Yields matching nodes as they are discovered across all applicable scopes.
    /// No buffering or sorting — results stream immediately.
    /// </summary>
    private async IAsyncEnumerable<object> FindMatchingNodesAsync(
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

        // Exact path matches
        foreach (var searchPath in pathsToSearch)
        {
            var node = await persistence.GetNodeSecureAsync(searchPath, userId, options, ct);
            if (node != null && _evaluator.Matches(node, parsedQuery)
                && !IsExcludedByContext(node, context)
                && !IsExcludedByIsMain(node, parsedQuery))
            {
                yield return node;
            }
        }

        // Children scope
        if (effectiveScope == QueryScope.Children)
        {
            await foreach (var child in persistence.GetChildrenSecureAsync(basePath, userId, options)
                .WithCancellation(ct))
            {
                if (_evaluator.Matches(child, parsedQuery) && !IsExcludedByContext(child, context)
                    && !IsExcludedByIsMain(child, parsedQuery))
                    yield return child;
            }
        }

        // Ancestor children (AncestorsAndSelf, Hierarchy, Ancestors)
        if (effectiveScope == QueryScope.AncestorsAndSelf
            || effectiveScope == QueryScope.Hierarchy
            || effectiveScope == QueryScope.Ancestors)
        {
            var pathsToSearchChildren = effectiveScope == QueryScope.Ancestors
                ? GetPathsForScope(basePath, QueryScope.Ancestors)
                : GetPathsForScope(basePath, QueryScope.AncestorsAndSelf);

            foreach (var ancestorPath in pathsToSearchChildren)
            {
                await foreach (var child in persistence.GetChildrenSecureAsync(
                    ancestorPath, userId, options).WithCancellation(ct))
                {
                    if (_evaluator.Matches(child, parsedQuery)
                        && !IsExcludedByContext(child, context)
                        && !IsExcludedByIsMain(child, parsedQuery))
                        yield return child;
                }
            }
        }

        // Descendants scope
        if (effectiveScope == QueryScope.Descendants
            || effectiveScope == QueryScope.Hierarchy
            || effectiveScope == QueryScope.Subtree)
        {
            await foreach (var descendant in persistence.GetDescendantsSecureAsync(
                basePath, userId, options).WithCancellation(ct))
            {
                if (_evaluator.Matches(descendant, parsedQuery)
                    && !IsExcludedByContext(descendant, context)
                    && !IsExcludedByIsMain(descendant, parsedQuery))
                    yield return descendant;
            }
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

    /// <summary>
    /// Validates a node read operation using INodeValidator instances from DI.
    /// Mirrors MeshCatalog.ValidateReadAsync logic.
    /// </summary>
    private async Task<bool> ValidateReadAsync(MeshNode node, string userId, CancellationToken ct = default)
    {
        if (nodeValidators == null)
            return true;

        // Always use the effective userId for the validation context.
        // The query's explicit UserId takes precedence over session AccessContext
        // to prevent admin context from leaking into public queries.
        var accessContext = !string.IsNullOrEmpty(userId) && userId != WellKnownUsers.Anonymous
            ? new AccessContext { ObjectId = userId }
            : accessService?.Context;

        var context = new NodeValidationContext
        {
            Operation = NodeOperation.Read,
            Node = node,
            AccessContext = accessContext
        };

        foreach (var validator in nodeValidators)
        {
            if (validator.SupportedOperations.Count > 0 &&
                !validator.SupportedOperations.Contains(NodeOperation.Read))
                continue;

            var result = await validator.ValidateAsync(context, ct);
            if (!result.IsValid)
            {
                logger?.LogDebug("Validator {Validator} rejected read on node {Path}: {Error}",
                    validator.GetType().Name, node.Path, result.ErrorMessage);
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public IAsyncEnumerable<QuerySuggestion> AutocompleteAsync(
        string basePath,
        string prefix,
        JsonSerializerOptions options,
        int limit = 10,
        CancellationToken ct = default)
        => AutocompleteAsync(basePath, prefix, options, null, AutocompleteMode.PathFirst, limit, null, null, ct);

    /// <inheritdoc />
    public IAsyncEnumerable<QuerySuggestion> AutocompleteAsync(
        string basePath,
        string prefix,
        JsonSerializerOptions options,
        AutocompleteMode mode,
        int limit = 10,
        string? contextPath = null,
        string? context = null,
        CancellationToken ct = default)
        => AutocompleteAsync(basePath, prefix, options, null, mode, limit, contextPath, context, ct);

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
        string? context = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(basePath);
        var normalizedPrefix = prefix ?? "";

        var suggestions = new List<QuerySuggestion>();

        // Search descendants for matching nodes (with security filtering)
        await foreach (var node in persistence.GetDescendantsSecureAsync(normalizedPath, userId, options).WithCancellation(ct))
        {
            // Skip node types excluded from autocomplete (configured via AddAutocompleteExcludedTypes)
            if (meshConfiguration?.AutocompleteExcludedNodeTypes.Contains(node.NodeType ?? "") == true)
                continue;

            // Context-based exclusion for autocomplete
            if (context != null && IsExcludedByContext(node, context))
                continue;

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
        var node = await persistence.GetNodeAsync(path, options, ct);
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
            if (changeNotifier == null)
            {
                observer.OnCompleted();
                return Disposable.Empty;
            }

            // Subscribe to changes with debouncing
            var changeBuffer = new Subject<DataChangeNotification>();
            var subscription = new CompositeDisposable();

            // Subscribe to the change notifier and filter by path/scope
            var notifierSubscription = changeNotifier
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

        // Supplement re-query results with entities from notifications
        // that haven't appeared in the file-system listing yet (write lag).
        foreach (var (path, change) in changesByPath)
        {
            if (change.Entity is T directMatch && _evaluator.Matches(directMatch, parsedQuery))
            {
                if (change.Kind == DataChangeKind.Deleted)
                    newItems.Remove(path);
                else
                    newItems[path] = directMatch;
            }
        }

        // Detect added and updated items (DistinctUntilChanged: only emit if value actually differs)
        foreach (var (path, item) in newItems)
        {
            if (currentItems.TryGetValue(path, out var existingItem))
            {
                // Item existed before - only emit Updated if the serializable content changed
                // (skip Func<> fields like HubConfiguration that break record Equals)
                if (!ItemEquals(existingItem, item))
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

    /// <summary>
    /// Compares two items for equality in the context of DistinctUntilChanged.
    /// For MeshNode, strips non-serializable fields (HubConfiguration, GlobalServiceConfigurations)
    /// before comparison to avoid false negatives from Func&lt;&gt; reference inequality.
    /// </summary>
    private static bool ItemEquals<T>(T a, T b)
    {
        if (a is MeshNode nodeA && b is MeshNode nodeB)
        {
            // Compare content-relevant fields only, stripping volatile/non-serializable fields
            return nodeA with { HubConfiguration = null, GlobalServiceConfigurations = [], LastModified = default, Version = 0 }
                == nodeB with { HubConfiguration = null, GlobalServiceConfigurations = [], LastModified = default, Version = 0 };
        }
        return Equals(a, b);
    }

    /// <summary>
    /// Checks whether a node should be excluded based on context.
    /// Checks both type-level exclusion (from MeshConfiguration) and node-level exclusion.
    /// </summary>
    private bool IsExcludedByContext(MeshNode node, string? context)
    {
        if (context == null) return false;
        if (meshConfiguration?.IsExcludedFromContext(node.NodeType, context) == true)
            return true;
        if (node.ExcludeFromContext?.Contains(context) == true)
            return true;
        return false;
    }

    /// <summary>
    /// Checks whether a node should be excluded by the is:main filter.
    /// Excludes satellite nodes (MainNode != null and MainNode != Path).
    /// </summary>
    private static bool IsExcludedByIsMain(MeshNode node, ParsedQuery query)
    {
        if (query.IsMain != true) return false;
        return node.MainNode != node.Path;
    }
}
