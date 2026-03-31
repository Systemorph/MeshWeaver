using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Data.Completion;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Completion;

/// <summary>
/// Provides context-aware autocomplete for Unified Content References (@ syntax).
///
/// When a contextPath is available (editing a specific node):
/// - "@" suggests children of the current node (relative paths)
/// - "@../" suggests siblings (parent's children)
/// - "@/" switches to absolute mode (global search across all partitions)
///
/// When no contextPath (top-level search):
/// - "@" suggests top-level nodes globally (absolute paths)
/// </summary>
internal class UnifiedReferenceAutocompleteProvider(
    IMeshCatalog? meshCatalog,
    IMeshService? meshQuery,
    INavigationService? navigationContext,
    IMessageHub hub) : IAutocompleteProvider
{
    private JsonSerializerOptions JsonOptions => hub.JsonSerializerOptions;

    private const int ContextPriority = 2000;
    private const int PrefixPriority = 1800;
    private const int KeywordPriority = 1500;
    private const int ItemPriority = 1000;

    /// <summary>
    /// Reserved keywords for unified references.
    /// </summary>
    private static readonly Dictionary<string, (string Description, AutocompleteKind Kind)> Keywords = new()
    {
        ["data/"] = ("Data collections and entities", AutocompleteKind.Other),
        ["content/"] = ("Content files (documents, images)", AutocompleteKind.File),
        ["area/"] = ("Layout areas and views", AutocompleteKind.Other),
        ["collection/"] = ("Collection definitions", AutocompleteKind.Other),
        ["schema/"] = ("JSON schemas and type definitions", AutocompleteKind.Other),
    };

    /// <inheritdoc />
    public async IAsyncEnumerable<AutocompleteItem> GetItemsAsync(
        string query,
        string? contextPath = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(query) || !query.StartsWith("@"))
            yield break;

        // Strip the @ prefix(es) - handle both @ and @@
        var path = query.TrimStart('@');

        // Determine effective context: prefer explicit contextPath, fall back to navigation context
        var effectiveContext = contextPath ?? navigationContext?.CurrentNamespace;

        // Check for absolute mode: @/ means search globally
        if (path.StartsWith("/"))
        {
            var absolutePath = path[1..]; // strip leading /
            await foreach (var item in GetAbsoluteSuggestions(absolutePath, ct))
                yield return item;
            yield break;
        }

        // If we have context, use relative path mode
        if (!string.IsNullOrEmpty(effectiveContext))
        {
            await foreach (var item in GetRelativeSuggestions(path, effectiveContext, ct))
                yield return item;
            yield break;
        }

        // No context — fall back to global absolute mode
        await foreach (var item in GetAbsoluteSuggestions(path, ct))
            yield return item;
    }

    /// <summary>
    /// Provides suggestions using relative paths from the current node context.
    /// Handles: "@child", "@../sibling", "@../../ancestor/child"
    /// </summary>
    private async IAsyncEnumerable<AutocompleteItem> GetRelativeSuggestions(
        string path,
        string contextPath,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Count and consume ../ prefixes to navigate up
        var relativePrefix = "";
        var searchBase = contextPath;
        while (path.StartsWith("../"))
        {
            relativePrefix += "../";
            path = path[3..];
            var lastSlash = searchBase.LastIndexOf('/');
            searchBase = lastSlash > 0 ? searchBase[..lastSlash] : "";
        }

        if (string.IsNullOrEmpty(searchBase) && string.IsNullOrEmpty(relativePrefix))
        {
            // At root with context but no ../ — search children of context node
            searchBase = contextPath;
        }

        // Parse remaining path segments
        var segments = path.Split('/', StringSplitOptions.None);
        var completedSegments = segments.SkipLast(1).ToArray();
        var currentSegment = segments.LastOrDefault() ?? "";
        var endsWithSlash = path.EndsWith("/");

        // Build the full search path by walking into completed segments
        if (completedSegments.Length > 0)
        {
            var subPath = string.Join("/", completedSegments);
            searchBase = string.IsNullOrEmpty(searchBase) ? subPath : $"{searchBase}/{subPath}";
            relativePrefix += string.Join("/", completedSegments) + "/";
        }

        if (meshQuery == null)
            yield break;

        // Check if current search base has a keyword prefix (data:, content:, etc.)
        var lastSegment = completedSegments.LastOrDefault()?.ToLowerInvariant();
        if (lastSegment != null && Keywords.ContainsKey(lastSegment + "/"))
        {
            // Keyword-specific suggestions
            await foreach (var item in GetRelativeKeywordSuggestions(searchBase, lastSegment, relativePrefix, currentSegment, ct))
                yield return item;
            yield break;
        }

        // Suggest keywords if we ended with a slash and have at least one completed segment
        if (endsWithSlash && completedSegments.Length > 0)
        {
            foreach (var item in GetRelativeKeywords(relativePrefix, currentSegment))
                yield return item;
        }

        // Suggest child nodes at the searchBase
        IAsyncEnumerable<QuerySuggestion>? suggestions = null;
        try
        {
            suggestions = meshQuery.AutocompleteAsync(searchBase ?? "", currentSegment, 15, ct);
        }
        catch { /* Ignore errors */ }

        if (suggestions != null)
        {
            await foreach (var suggestion in suggestions.WithCancellation(ct))
            {
                // Get the child name (last segment of the suggestion path)
                var childName = suggestion.Name;

                yield return new AutocompleteItem(
                    Label: childName,
                    InsertText: $"@{relativePrefix}{childName}/",
                    Description: suggestion.NodeType ?? "Node",
                    Category: string.IsNullOrEmpty(relativePrefix) ? "Children" : "Nodes",
                    Priority: ContextPriority,
                    Kind: AutocompleteKind.Other
                );
            }
        }
    }

    private IEnumerable<AutocompleteItem> GetRelativeKeywords(string relativePrefix, string prefix)
    {
        return Keywords
            .Where(kv => string.IsNullOrEmpty(prefix) ||
                        kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(kv => new AutocompleteItem(
                Label: kv.Key,
                InsertText: $"@{relativePrefix}{kv.Key}",
                Description: kv.Value.Description,
                Category: "Keywords",
                Priority: KeywordPriority,
                Kind: kv.Value.Kind
            ));
    }

    private async IAsyncEnumerable<AutocompleteItem> GetRelativeKeywordSuggestions(
        string searchBase,
        string keyword,
        string relativePrefix,
        string currentSegment,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (meshQuery == null)
            yield break;

        IAsyncEnumerable<QuerySuggestion>? suggestions = null;
        try
        {
            suggestions = meshQuery.AutocompleteAsync(searchBase, currentSegment, 15, ct);
        }
        catch { /* Ignore errors */ }

        if (suggestions != null)
        {
            await foreach (var suggestion in suggestions.WithCancellation(ct))
            {
                yield return new AutocompleteItem(
                    Label: suggestion.Name,
                    InsertText: $"@{relativePrefix}{suggestion.Name} ",
                    Description: suggestion.NodeType ?? GetKeywordItemDescription(keyword),
                    Category: GetKeywordCategory(keyword),
                    Priority: ItemPriority,
                    Kind: GetKeywordKind(keyword)
                );
            }
        }
    }

    /// <summary>
    /// Provides suggestions using absolute paths (global search).
    /// Triggered by @/ or when no context is available.
    /// </summary>
    private async IAsyncEnumerable<AutocompleteItem> GetAbsoluteSuggestions(
        string path,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var segments = path.Split('/', StringSplitOptions.None);
        var completedSegments = segments.SkipLast(1).ToArray();
        var currentSegment = segments.LastOrDefault() ?? "";
        var endsWithSlash = path.EndsWith("/");

        await foreach (var item in GetAbsoluteSuggestionsForStage(completedSegments, currentSegment, endsWithSlash, ct))
            yield return item;
    }

    private async IAsyncEnumerable<AutocompleteItem> GetAbsoluteSuggestionsForStage(
        string[] completedSegments,
        string currentSegment,
        bool endsWithSlash,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Stage 1: No completed segments yet - suggest top-level addresses
        if (completedSegments.Length == 0)
        {
            await foreach (var item in GetTopLevelSuggestions(currentSegment, ct))
                yield return item;
            yield break;
        }

        // Build the address from completed segments
        var address = string.Join("/", completedSegments);

        // If we ended with a slash after at least 2 segments, suggest keywords + children
        if (completedSegments.Length >= 2 && endsWithSlash)
        {
            foreach (var item in GetAbsoluteKeywordSuggestions(address, currentSegment))
                yield return item;
        }

        // Check for keyword in segments
        if (completedSegments.Length >= 3)
        {
            var potentialKeyword = completedSegments.Last().ToLowerInvariant();
            if (Keywords.ContainsKey(potentialKeyword + "/"))
            {
                var keywordAddress = string.Join("/", completedSegments.SkipLast(1));
                await foreach (var item in GetAbsoluteKeywordSpecificSuggestions(
                    keywordAddress, potentialKeyword, currentSegment, ct))
                    yield return item;
                yield break;
            }
        }

        // Suggest children at current path
        if (meshQuery != null)
        {
            IAsyncEnumerable<QuerySuggestion>? suggestions = null;
            try
            {
                suggestions = meshQuery.AutocompleteAsync(address, currentSegment, 15, ct);
            }
            catch { /* Ignore errors */ }

            if (suggestions != null)
            {
                await foreach (var suggestion in suggestions.WithCancellation(ct))
                {
                    yield return new AutocompleteItem(
                        Label: $"{suggestion.Name}/",
                        InsertText: $"@/{suggestion.Path}/",
                        Description: suggestion.NodeType ?? "Node",
                        Category: "Nodes",
                        Priority: ItemPriority,
                        Kind: AutocompleteKind.Other
                    );
                }
            }
        }
    }

    private async IAsyncEnumerable<AutocompleteItem> GetTopLevelSuggestions(
        string prefix,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var addedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Top-level nodes from mesh query (root level)
        if (meshQuery != null)
        {
            IAsyncEnumerable<QuerySuggestion>? suggestions = null;
            try
            {
                suggestions = meshQuery.AutocompleteAsync("", prefix, 15, ct);
            }
            catch { /* Ignore errors */ }

            if (suggestions != null)
            {
                await foreach (var suggestion in suggestions.WithCancellation(ct))
                {
                    if (addedPaths.Add(suggestion.Path))
                    {
                        yield return new AutocompleteItem(
                            Label: $"{suggestion.Name}/",
                            InsertText: $"@/{suggestion.Path}/",
                            Description: suggestion.NodeType ?? suggestion.Name,
                            Category: "Addresses",
                            Priority: PrefixPriority,
                            Kind: AutocompleteKind.Other
                        );
                    }
                }
            }
        }

        // Type definitions from mesh catalog configuration
        if (meshCatalog != null)
        {
            var topLevelNodes = meshCatalog.Configuration.Nodes.Values
                .Where(n => n.Segments.Count == 1)
                .Where(n => string.IsNullOrEmpty(prefix) ||
                           n.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n.Order ?? int.MaxValue)
                .ThenBy(n => n.Name)
                .Take(10);

            foreach (var node in topLevelNodes)
            {
                if (addedPaths.Add(node.Path))
                {
                    yield return new AutocompleteItem(
                        Label: $"{node.Path}/",
                        InsertText: $"@/{node.Path}/",
                        Description: node.Name,
                        Category: "Types",
                        Priority: PrefixPriority - (node.Order ?? 0),
                        Kind: AutocompleteKind.Other
                    );
                }
            }
        }
    }

    private IEnumerable<AutocompleteItem> GetAbsoluteKeywordSuggestions(string address, string prefix)
    {
        return Keywords
            .Where(kv => string.IsNullOrEmpty(prefix) ||
                        kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(kv => new AutocompleteItem(
                Label: kv.Key,
                InsertText: $"@/{address}/{kv.Key}",
                Description: kv.Value.Description,
                Category: "Keywords",
                Priority: KeywordPriority,
                Kind: kv.Value.Kind
            ));
    }

    private async IAsyncEnumerable<AutocompleteItem> GetAbsoluteKeywordSpecificSuggestions(
        string address,
        string keyword,
        string prefix,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (meshQuery == null)
            yield break;

        IAsyncEnumerable<QuerySuggestion>? suggestions = null;
        try
        {
            suggestions = meshQuery.AutocompleteAsync(address, prefix, 15, ct);
        }
        catch { /* Ignore errors */ }

        if (suggestions != null)
        {
            await foreach (var suggestion in suggestions.WithCancellation(ct))
            {
                yield return new AutocompleteItem(
                    Label: suggestion.Name,
                    InsertText: $"@/{address}/{keyword}/{suggestion.Name} ",
                    Description: suggestion.NodeType ?? GetKeywordItemDescription(keyword),
                    Category: GetKeywordCategory(keyword),
                    Priority: ItemPriority,
                    Kind: GetKeywordKind(keyword)
                );
            }
        }
    }

    private static string GetKeywordItemDescription(string keyword) => keyword switch
    {
        "data" => "Data collection",
        "content" => "Content file",
        "area" => "Layout area",
        "collection" => "Collection definition",
        "schema" => "Type schema",
        _ => "Item"
    };

    private static string GetKeywordCategory(string keyword) => keyword switch
    {
        "data" => "Data",
        "content" => "Content",
        "area" => "Areas",
        "collection" => "Collections",
        "schema" => "Schemas",
        _ => "Items"
    };

    private static AutocompleteKind GetKeywordKind(string keyword) => keyword switch
    {
        "content" => AutocompleteKind.File,
        _ => AutocompleteKind.Other
    };
}
