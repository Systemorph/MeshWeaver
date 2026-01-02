using System.Runtime.CompilerServices;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Query;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Persistence.Query;

/// <summary>
/// In-memory implementation of IMeshQuery.
/// Extracts query functionality from InMemoryPersistenceService for use as a standalone service.
/// </summary>
public class InMemoryMeshQuery : IMeshQuery
{
    private readonly IPersistenceService _persistence;
    private readonly QueryParser _parser = new();
    private readonly QueryEvaluator _evaluator = new();

    public InMemoryMeshQuery(IPersistenceService persistence)
    {
        _persistence = persistence;
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

        // Handle source:activity from request
        if (request.IncludeActivities && parsedQuery.Source != QuerySource.Activity)
        {
            parsedQuery = parsedQuery with { Source = QuerySource.Activity };
        }

        var basePath = NormalizePath(request.BasePath);

        // Determine paths to search based on scope
        var pathsToSearch = GetPathsForScope(basePath, parsedQuery.Scope);

        // Collect results with fuzzy scores for ordering
        var results = new List<(object Item, int Score)>();

        foreach (var searchPath in pathsToSearch)
        {
            // Search MeshNodes at this path
            var node = await _persistence.GetNodeAsync(searchPath, ct);
            if (node != null)
            {
                if (MatchesQuery(node, parsedQuery, request.Namespace))
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

        // If we're doing scope=descendants, also search descendant paths
        if (parsedQuery.Scope == QueryScope.Descendants || parsedQuery.Scope == QueryScope.Hierarchy)
        {
            await foreach (var descendant in _persistence.GetDescendantsAsync(basePath).WithCancellation(ct))
            {
                var descendantPath = NormalizePath(descendant.Path);

                // Check namespace restriction
                if (!string.IsNullOrEmpty(request.Namespace) &&
                    !descendantPath.StartsWith(request.Namespace, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Evaluate the node itself
                if (MatchesQuery(descendant, parsedQuery, request.Namespace))
                {
                    var score = _evaluator.GetFuzzyScore(descendant, parsedQuery.TextSearch);
                    // Avoid duplicates
                    if (!results.Any(r => ReferenceEquals(r.Item, descendant)))
                        results.Add((descendant, score));
                }

                // Search partition objects under descendant
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

        // Apply limit
        if (parsedQuery.Limit.HasValue && parsedQuery.Limit.Value > 0)
        {
            orderedResults = _evaluator.LimitResults(orderedResults, parsedQuery.Limit);
        }

        foreach (var item in orderedResults)
        {
            yield return item;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QuerySuggestion> AutocompleteAsync(
        string basePath,
        string prefix,
        int limit = 10,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(basePath);
        var normalizedPrefix = prefix?.ToLowerInvariant() ?? "";

        var suggestions = new List<QuerySuggestion>();

        // Search descendants for matching nodes
        await foreach (var node in _persistence.GetDescendantsAsync(normalizedPath).WithCancellation(ct))
        {
            var name = node.Name ?? node.Id;
            var nameLower = name.ToLowerInvariant();

            // Calculate match score based on prefix match
            double score = 0;
            if (nameLower.StartsWith(normalizedPrefix))
            {
                score = 100 - (nameLower.Length - normalizedPrefix.Length); // Exact prefix match, shorter is better
            }
            else if (nameLower.Contains(normalizedPrefix))
            {
                score = 50 - (nameLower.IndexOf(normalizedPrefix, StringComparison.OrdinalIgnoreCase)); // Contains match
            }
            else if (FuzzyMatch(nameLower, normalizedPrefix))
            {
                score = 25; // Fuzzy match
            }

            if (score > 0)
            {
                suggestions.Add(new QuerySuggestion(node.Path, name, node.NodeType, score));
            }
        }

        // Order by score descending and apply limit
        foreach (var suggestion in suggestions.OrderByDescending(s => s.Score).Take(limit))
        {
            yield return suggestion;
        }
    }

    private bool MatchesQuery(MeshNode node, ParsedQuery parsedQuery, string? namespaceRestriction)
    {
        // Check namespace restriction first
        if (!string.IsNullOrEmpty(namespaceRestriction))
        {
            var nodePath = NormalizePath(node.Path);
            if (!nodePath.StartsWith(namespaceRestriction, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return _evaluator.Matches(node, parsedQuery);
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

        // Always include the base path for Exact, Ancestors, Hierarchy
        if (scope != QueryScope.Descendants || string.IsNullOrEmpty(basePath))
        {
            paths.Add(basePath);
        }

        // Add ancestor paths for Ancestors and Hierarchy scopes
        if (scope == QueryScope.Ancestors || scope == QueryScope.Hierarchy)
        {
            var segments = basePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int i = segments.Length - 1; i >= 0; i--)
            {
                var ancestorPath = string.Join("/", segments.Take(i));
                if (!paths.Contains(ancestorPath, StringComparer.OrdinalIgnoreCase))
                    paths.Add(ancestorPath);
            }
        }

        // For Descendants scope, the base path is also searched
        if (scope == QueryScope.Descendants)
        {
            paths.Add(basePath);
        }

        return paths;
    }
}
