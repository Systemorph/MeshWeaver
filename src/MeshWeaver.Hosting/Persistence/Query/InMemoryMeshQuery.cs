using System.Runtime.CompilerServices;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Query;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

namespace MeshWeaver.Hosting.Persistence.Query;

/// <summary>
/// In-memory implementation of IMeshQuery.
/// Extracts query functionality from InMemoryPersistenceService for use as a standalone service.
/// </summary>
public class InMemoryMeshQuery : IMeshQuery
{
    private readonly IPersistenceService _persistence;
    private readonly INavigationService? _navigationContext;
    private readonly ISecurityService? _securityService;
    private readonly AccessService? _accessService;
    private readonly QueryParser _parser = new();
    private readonly QueryEvaluator _evaluator = new();

    public InMemoryMeshQuery(
        IPersistenceService persistence,
        INavigationService? navigationContext = null,
        ISecurityService? securityService = null,
        AccessService? accessService = null)
    {
        _persistence = persistence;
        _navigationContext = navigationContext;
        _securityService = securityService;
        _accessService = accessService;
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
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var parsedQuery = _parser.Parse(request.Query);

        // Override limit from request if provided
        if (request.Limit.HasValue)
        {
            parsedQuery = parsedQuery with { Limit = request.Limit };
        }

        // If no path is specified, use navigation context's namespace or default to root
        var effectivePath = parsedQuery.Path;
        var effectiveScope = parsedQuery.Scope;
        if (string.IsNullOrEmpty(effectivePath))
        {
            if (_navigationContext?.CurrentNamespace != null)
            {
                effectivePath = _navigationContext.CurrentNamespace;
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
            var node = await _persistence.GetNodeSecureAsync(searchPath, userId, ct);
            if (node != null)
            {
                if (_evaluator.Matches(node, parsedQuery))
                {
                    var score = _evaluator.GetFuzzyScore(node, parsedQuery.TextSearch);
                    results.Add((node, score));
                }
            }

            // Search partition objects at this path
            await foreach (var obj in _persistence.GetPartitionObjectsAsync(searchPath).WithCancellation(ct))
            {
                if (_evaluator.Matches(obj, parsedQuery))
                {
                    var score = _evaluator.GetFuzzyScore(obj, parsedQuery.TextSearch);
                    results.Add((obj, score));
                }
            }
        }

        // If we're doing scope=children, search immediate children only
        if (effectiveScope == QueryScope.Children)
        {
            await foreach (var child in _persistence.GetChildrenSecureAsync(basePath, userId).WithCancellation(ct))
            {
                // Evaluate the node itself
                if (_evaluator.Matches(child, parsedQuery))
                {
                    var score = _evaluator.GetFuzzyScore(child, parsedQuery.TextSearch);
                    // Avoid duplicates
                    if (!results.Any(r => ReferenceEquals(r.Item, child)))
                        results.Add((child, score));
                }

                // Search partition objects under child
                var childPath = NormalizePath(child.Path);
                await foreach (var obj in _persistence.GetPartitionObjectsAsync(childPath).WithCancellation(ct))
                {
                    if (_evaluator.Matches(obj, parsedQuery))
                    {
                        var score = _evaluator.GetFuzzyScore(obj, parsedQuery.TextSearch);
                        results.Add((obj, score));
                    }
                }
            }
        }

        // If we're doing scope=descendants, also search descendant paths recursively
        if (effectiveScope == QueryScope.Descendants || effectiveScope == QueryScope.Hierarchy || effectiveScope == QueryScope.Subtree)
        {
            await foreach (var descendant in _persistence.GetDescendantsSecureAsync(basePath, userId).WithCancellation(ct))
            {
                // Evaluate the node itself
                if (_evaluator.Matches(descendant, parsedQuery))
                {
                    var score = _evaluator.GetFuzzyScore(descendant, parsedQuery.TextSearch);
                    // Avoid duplicates
                    if (!results.Any(r => ReferenceEquals(r.Item, descendant)))
                        results.Add((descendant, score));
                }

                // Search partition objects under descendant
                var descendantPath = NormalizePath(descendant.Path);
                await foreach (var obj in _persistence.GetPartitionObjectsAsync(descendantPath).WithCancellation(ct))
                {
                    if (_evaluator.Matches(obj, parsedQuery))
                    {
                        var score = _evaluator.GetFuzzyScore(obj, parsedQuery.TextSearch);
                        results.Add((obj, score));
                    }
                }
            }
        }

        // Order by fuzzy score (higher first) for text searches, otherwise preserve order
        IEnumerable<object> orderedResults;
        if (!string.IsNullOrEmpty(parsedQuery.TextSearch))
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
        int limit = 10,
        CancellationToken ct = default)
        => AutocompleteAsync(basePath, prefix, null, limit, ct);

    /// <summary>
    /// Autocomplete with user ID for access control filtering.
    /// </summary>
    public async IAsyncEnumerable<QuerySuggestion> AutocompleteAsync(
        string basePath,
        string prefix,
        string? userId,
        int limit = 10,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(basePath);
        var normalizedPrefix = prefix?.ToLowerInvariant() ?? "";

        var suggestions = new List<QuerySuggestion>();

        // Search descendants for matching nodes (with security filtering)
        await foreach (var node in _persistence.GetDescendantsSecureAsync(normalizedPath, userId).WithCancellation(ct))
        {
            var name = node.Name ?? node.Id;
            var nameLower = name.ToLowerInvariant();
            var pathLower = node.Path.ToLowerInvariant();

            // Calculate match score based on prefix match (check both name and path)
            double score = 0;

            // Name matches (higher priority)
            if (nameLower.StartsWith(normalizedPrefix))
            {
                score = 100 - (nameLower.Length - normalizedPrefix.Length); // Exact prefix match, shorter is better
            }
            else if (nameLower.Contains(normalizedPrefix))
            {
                score = 50 - (nameLower.IndexOf(normalizedPrefix, StringComparison.OrdinalIgnoreCase)); // Contains match
            }
            // Path matches (lower priority than name)
            else if (pathLower.Contains(normalizedPrefix))
            {
                score = 30 - (pathLower.IndexOf(normalizedPrefix, StringComparison.OrdinalIgnoreCase) * 0.1); // Path contains match
            }
            else if (FuzzyMatch(nameLower, normalizedPrefix))
            {
                score = 25; // Fuzzy match on name
            }

            if (score > 0)
            {
                suggestions.Add(new QuerySuggestion(node.Path, name, node.NodeType, score));
            }
        }

        // Order by path length (shorter first), then score descending, then name alphabetically
        foreach (var suggestion in suggestions
            .OrderBy(s => s.Path.Length)
            .ThenByDescending(s => s.Score)
            .ThenBy(s => s.Name)
            .Take(limit))
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
}
