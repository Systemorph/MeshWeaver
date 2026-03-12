using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Data.Completion;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Completion;

/// <summary>
/// Provides context-aware autocomplete for Unified Content References (@ syntax).
/// Orchestrates suggestions based on the current query stage:
/// - Stage 1: Just "@" → suggest addresses from current namespace first, then global prefixes
/// - Stage 2: "@app/Northwind/" → suggest keywords (data/, area/, content/, etc.)
/// - Stage 3: "@app/Northwind/data/" → suggest specific items (collections, areas, files)
/// </summary>
internal class UnifiedReferenceAutocompleteProvider(
    IMeshCatalog? meshCatalog,
    IMeshService? meshQuery,
    INavigationService? navigationContext,
    IMessageHub hub) : IAutocompleteProvider
{
    private JsonSerializerOptions JsonOptions => hub.JsonSerializerOptions;

    private const int ContextPriority = 2000;  // Higher priority for context-aware items
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
        // Query format from Monaco: "@path" or "@path/" or "@path/keyword/" etc.
        if (string.IsNullOrEmpty(query) || !query.StartsWith("@"))
            yield break;

        // Strip the @ prefix(es) - handle both @ and @@
        var path = query.TrimStart('@');

        // Parse the path to determine current stage
        var segments = path.Split('/', StringSplitOptions.None);
        var completedSegments = segments.SkipLast(1).ToArray();
        var currentSegment = segments.LastOrDefault() ?? "";
        var endsWithSlash = path.EndsWith("/");

        await foreach (var item in GetSuggestionsForStage(completedSegments, currentSegment, endsWithSlash, ct))
        {
            yield return item;
        }
    }

    private async IAsyncEnumerable<AutocompleteItem> GetSuggestionsForStage(
        string[] completedSegments,
        string currentSegment,
        bool endsWithSlash,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Stage 1: No completed segments yet - suggest addresses
        if (completedSegments.Length == 0)
        {
            await foreach (var item in GetAddressSuggestions(currentSegment, ct))
                yield return item;
            yield break;
        }

        // Stage 2: Have addressType but maybe not addressId
        if (completedSegments.Length == 1 && !endsWithSlash)
        {
            // Still completing address, suggest address IDs
            await foreach (var item in GetAddressIdSuggestions(completedSegments[0], currentSegment, ct))
                yield return item;
            yield break;
        }

        // Have at least addressType/addressId
        var addressType = completedSegments[0];
        var addressId = completedSegments.Length > 1 ? completedSegments[1] : "";
        var address = $"{addressType}/{addressId}";

        // Stage 3: Have address, check for keyword
        if (completedSegments.Length == 2 && endsWithSlash)
        {
            // Just completed address, suggest keywords
            foreach (var item in GetKeywordSuggestions(address, currentSegment))
                yield return item;
            // Also suggest layout areas (default when no keyword)
            await foreach (var item in GetLayoutAreaSuggestions(address, currentSegment, ct))
                yield return item;
            yield break;
        }

        if (completedSegments.Length >= 3)
        {
            var potentialKeyword = completedSegments[2].ToLowerInvariant();

            // Check if third segment is a keyword
            if (Keywords.ContainsKey(potentialKeyword + "/"))
            {
                // Stage 4: Have address and keyword, suggest specific items
                var remainingPath = string.Join("/", completedSegments.Skip(3));
                await foreach (var item in GetKeywordSpecificSuggestions(
                    address, potentialKeyword, remainingPath, currentSegment, ct))
                    yield return item;
                yield break;
            }
        }

        // Default: treat as area reference
        if (completedSegments.Length >= 2)
        {
            await foreach (var item in GetLayoutAreaSuggestions(address, currentSegment, ct))
                yield return item;
        }
    }

    private async IAsyncEnumerable<AutocompleteItem> GetAddressSuggestions(
        string prefix,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var currentNamespace = navigationContext?.CurrentNamespace;
        var addedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // First, suggest items from current namespace context if available
        if (!string.IsNullOrEmpty(currentNamespace) && meshQuery != null)
        {
            IAsyncEnumerable<QuerySuggestion>? suggestions = null;
            try
            {
                suggestions = meshQuery.AutocompleteAsync(currentNamespace, prefix, 10, ct);
            }
            catch
            {
                // Ignore errors from mesh query
            }

            if (suggestions != null)
            {
                await foreach (var suggestion in suggestions.WithCancellation(ct))
                {
                    if (addedPaths.Add(suggestion.Path))
                    {
                        yield return new AutocompleteItem(
                            Label: suggestion.Name,
                            InsertText: $"@{suggestion.Path}/",
                            Description: $"From current context: {suggestion.NodeType ?? "Node"}",
                            Category: "Nearby",
                            Priority: ContextPriority,
                            Kind: AutocompleteKind.Other
                        );
                    }
                }
            }
        }

        // Then add top-level nodes from mesh query (root level)
        if (meshQuery != null)
        {
            IAsyncEnumerable<QuerySuggestion>? suggestions = null;
            try
            {
                // Query from root (empty base path) to get all top-level nodes
                suggestions = meshQuery.AutocompleteAsync("", prefix, 15, ct);
            }
            catch
            {
                // Ignore errors from mesh query
            }

            if (suggestions != null)
            {
                await foreach (var suggestion in suggestions.WithCancellation(ct))
                {
                    if (addedPaths.Add(suggestion.Path))
                    {
                        yield return new AutocompleteItem(
                            Label: $"{suggestion.Name}/",
                            InsertText: $"@{suggestion.Path}/",
                            Description: suggestion.NodeType ?? suggestion.Name,
                            Category: "Addresses",
                            Priority: PrefixPriority,
                            Kind: AutocompleteKind.Other
                        );
                    }
                }
            }
        }

        // Also add type definitions from mesh catalog configuration
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
                        InsertText: $"@{node.Path}/",
                        Description: node.Name,
                        Category: "Types",
                        Priority: PrefixPriority - (node.Order ?? 0),
                        Kind: AutocompleteKind.Other
                    );
                }
            }
        }
    }

    private async IAsyncEnumerable<AutocompleteItem> GetAddressIdSuggestions(
        string addressType,
        string prefix,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (meshQuery == null)
            yield break;

        IAsyncEnumerable<QuerySuggestion>? suggestions = null;
        try
        {
            suggestions = meshQuery.AutocompleteAsync(addressType, prefix, 15, ct);
        }
        catch
        {
            // Ignore errors
        }

        if (suggestions != null)
        {
            await foreach (var suggestion in suggestions.WithCancellation(ct))
            {
                yield return new AutocompleteItem(
                    Label: suggestion.Name,
                    InsertText: $"@{suggestion.Path}/",
                    Description: suggestion.NodeType ?? "Address",
                    Category: addressType,
                    Priority: ItemPriority,
                    Kind: AutocompleteKind.Other
                );
            }
        }
    }

    private IEnumerable<AutocompleteItem> GetKeywordSuggestions(string address, string prefix)
    {
        return Keywords
            .Where(kv => string.IsNullOrEmpty(prefix) ||
                        kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(kv => new AutocompleteItem(
                Label: kv.Key,
                InsertText: $"@{address}/{kv.Key}",
                Description: kv.Value.Description,
                Category: "Keywords",
                Priority: KeywordPriority,
                Kind: kv.Value.Kind
            ));
    }

    private async IAsyncEnumerable<AutocompleteItem> GetLayoutAreaSuggestions(
        string address,
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
        catch
        {
            // Ignore errors
        }

        if (suggestions != null)
        {
            await foreach (var suggestion in suggestions.WithCancellation(ct))
            {
                yield return new AutocompleteItem(
                    Label: suggestion.Name,
                    InsertText: $"@{suggestion.Path} ",
                    Description: suggestion.NodeType ?? "Layout Area",
                    Category: "Areas",
                    Priority: ItemPriority,
                    Kind: AutocompleteKind.Other
                );
            }
        }
    }

    private async IAsyncEnumerable<AutocompleteItem> GetKeywordSpecificSuggestions(
        string address,
        string keyword,
        string basePath,
        string prefix,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (meshQuery == null)
            yield break;

        var searchPath = string.IsNullOrEmpty(basePath) ? address : $"{address}/{basePath}";

        IAsyncEnumerable<QuerySuggestion>? suggestions = null;
        try
        {
            suggestions = meshQuery.AutocompleteAsync(searchPath, prefix, 15, ct);
        }
        catch
        {
            // Ignore errors
        }

        if (suggestions != null)
        {
            await foreach (var suggestion in suggestions.WithCancellation(ct))
            {
                var insertPath = string.IsNullOrEmpty(basePath)
                    ? $"@{address}/{keyword}/{suggestion.Name}"
                    : $"@{address}/{keyword}/{basePath}/{suggestion.Name}";

                // Add trailing space for completed items, slash for containers
                var isContainer = suggestion.NodeType != null &&
                    (suggestion.NodeType.Contains("Collection") || suggestion.NodeType.Contains("Folder"));
                insertPath += isContainer ? "/" : " ";

                yield return new AutocompleteItem(
                    Label: suggestion.Name,
                    InsertText: insertPath,
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
