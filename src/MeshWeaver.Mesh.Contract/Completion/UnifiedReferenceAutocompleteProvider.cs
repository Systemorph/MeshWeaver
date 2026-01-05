using MeshWeaver.Data.Completion;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Mesh.Completion;

/// <summary>
/// Provides context-aware autocomplete for Unified Content References (@ syntax).
/// Orchestrates suggestions based on the current query stage:
/// - Stage 1: Just "@" → suggest addresses from current namespace first, then global prefixes
/// - Stage 2: "@app/Northwind/" → suggest keywords (data/, area/, content/, etc.)
/// - Stage 3: "@app/Northwind/data/" → suggest specific items (collections, areas, files)
/// </summary>
public class UnifiedReferenceAutocompleteProvider(
    IMeshCatalog? meshCatalog,
    IMeshQuery? meshQuery,
    INavigationContextService? navigationContext) : IAutocompleteProvider
{
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
    public async Task<IEnumerable<AutocompleteItem>> GetItemsAsync(string query, CancellationToken ct = default)
    {
        // Query format from Monaco: "@path" or "@path/" or "@path/keyword/" etc.
        if (string.IsNullOrEmpty(query) || !query.StartsWith("@"))
            return [];

        // Strip the @ prefix(es) - handle both @ and @@
        var path = query.TrimStart('@');

        // Parse the path to determine current stage
        var segments = path.Split('/', StringSplitOptions.None);
        var completedSegments = segments.SkipLast(1).ToArray();
        var currentSegment = segments.LastOrDefault() ?? "";
        var endsWithSlash = path.EndsWith("/");

        return await GetSuggestionsForStage(completedSegments, currentSegment, endsWithSlash, ct);
    }

    private async Task<IEnumerable<AutocompleteItem>> GetSuggestionsForStage(
        string[] completedSegments,
        string currentSegment,
        bool endsWithSlash,
        CancellationToken ct)
    {
        var items = new List<AutocompleteItem>();

        // Stage 1: No completed segments yet - suggest addresses
        if (completedSegments.Length == 0)
        {
            items.AddRange(await GetAddressSuggestions(currentSegment, ct));
            return items;
        }

        // Stage 2: Have addressType but maybe not addressId
        if (completedSegments.Length == 1 && !endsWithSlash)
        {
            // Still completing address, suggest address IDs
            items.AddRange(await GetAddressIdSuggestions(completedSegments[0], currentSegment, ct));
            return items;
        }

        // Have at least addressType/addressId
        var addressType = completedSegments[0];
        var addressId = completedSegments.Length > 1 ? completedSegments[1] : "";
        var address = $"{addressType}/{addressId}";

        // Stage 3: Have address, check for keyword
        if (completedSegments.Length == 2 && endsWithSlash)
        {
            // Just completed address, suggest keywords
            items.AddRange(GetKeywordSuggestions(address, currentSegment));
            // Also suggest layout areas (default when no keyword)
            items.AddRange(await GetLayoutAreaSuggestions(address, currentSegment, ct));
            return items;
        }

        if (completedSegments.Length >= 3)
        {
            var potentialKeyword = completedSegments[2].ToLowerInvariant();

            // Check if third segment is a keyword
            if (Keywords.ContainsKey(potentialKeyword + "/"))
            {
                // Stage 4: Have address and keyword, suggest specific items
                var remainingPath = string.Join("/", completedSegments.Skip(3));
                items.AddRange(await GetKeywordSpecificSuggestions(
                    address, potentialKeyword, remainingPath, currentSegment, ct));
                return items;
            }
        }

        // Default: treat as area reference
        if (completedSegments.Length >= 2)
        {
            var areaPath = string.Join("/", completedSegments.Skip(2));
            items.AddRange(await GetLayoutAreaSuggestions(address, currentSegment, ct));
        }

        return items;
    }

    private async Task<IEnumerable<AutocompleteItem>> GetAddressSuggestions(string prefix, CancellationToken ct)
    {
        var items = new List<AutocompleteItem>();
        var currentNamespace = navigationContext?.CurrentNamespace;
        var addedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // First, suggest items from current namespace context if available
        if (!string.IsNullOrEmpty(currentNamespace) && meshQuery != null)
        {
            try
            {
                await foreach (var suggestion in meshQuery.AutocompleteAsync(currentNamespace, prefix, 10, ct))
                {
                    if (addedPaths.Add(suggestion.Path))
                    {
                        items.Add(new AutocompleteItem(
                            Label: suggestion.Name,
                            InsertText: $"@{suggestion.Path}/",
                            Description: $"From current context: {suggestion.NodeType ?? "Node"}",
                            Category: "Nearby",
                            Priority: ContextPriority,
                            Kind: AutocompleteKind.Other
                        ));
                    }
                }
            }
            catch
            {
                // Ignore errors from mesh query
            }
        }

        // Then add top-level nodes from mesh query (root level)
        if (meshQuery != null)
        {
            try
            {
                // Query from root (empty base path) to get all top-level nodes
                await foreach (var suggestion in meshQuery.AutocompleteAsync("", prefix, 15, ct))
                {
                    if (addedPaths.Add(suggestion.Path))
                    {
                        items.Add(new AutocompleteItem(
                            Label: $"{suggestion.Name}/",
                            InsertText: $"@{suggestion.Path}/",
                            Description: suggestion.NodeType ?? suggestion.Name,
                            Category: "Addresses",
                            Priority: PrefixPriority,
                            Kind: AutocompleteKind.Other
                        ));
                    }
                }
            }
            catch
            {
                // Ignore errors from mesh query
            }
        }

        // Also add type definitions from mesh catalog configuration
        if (meshCatalog != null)
        {
            var topLevelNodes = meshCatalog.Configuration.Nodes.Values
                .Where(n => n.Segments.Count == 1)
                .Where(n => string.IsNullOrEmpty(prefix) ||
                           n.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n.DisplayOrder)
                .ThenBy(n => n.Name)
                .Take(10);

            foreach (var node in topLevelNodes)
            {
                if (addedPaths.Add(node.Path))
                {
                    items.Add(new AutocompleteItem(
                        Label: $"{node.Path}/",
                        InsertText: $"@{node.Path}/",
                        Description: node.Description ?? node.Name,
                        Category: "Types",
                        Priority: PrefixPriority - node.DisplayOrder,
                        Kind: AutocompleteKind.Other
                    ));
                }
            }
        }

        return items;
    }

    private async Task<IEnumerable<AutocompleteItem>> GetAddressIdSuggestions(
        string addressType, string prefix, CancellationToken ct)
    {
        var items = new List<AutocompleteItem>();

        if (meshQuery != null)
        {
            try
            {
                await foreach (var suggestion in meshQuery.AutocompleteAsync(addressType, prefix, 15, ct))
                {
                    items.Add(new AutocompleteItem(
                        Label: suggestion.Name,
                        InsertText: $"@{suggestion.Path}/",
                        Description: suggestion.NodeType ?? "Address",
                        Category: addressType,
                        Priority: ItemPriority,
                        Kind: AutocompleteKind.Other
                    ));
                }
            }
            catch
            {
                // Ignore errors
            }
        }

        return items;
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

    private async Task<IEnumerable<AutocompleteItem>> GetLayoutAreaSuggestions(
        string address, string prefix, CancellationToken ct)
    {
        var items = new List<AutocompleteItem>();

        // Use mesh query to find layout areas under this address
        if (meshQuery != null)
        {
            try
            {
                await foreach (var suggestion in meshQuery.AutocompleteAsync(address, prefix, 15, ct))
                {
                    items.Add(new AutocompleteItem(
                        Label: suggestion.Name,
                        InsertText: $"@{suggestion.Path} ",
                        Description: suggestion.NodeType ?? "Layout Area",
                        Category: "Areas",
                        Priority: ItemPriority,
                        Kind: AutocompleteKind.Other
                    ));
                }
            }
            catch
            {
                // Ignore errors
            }
        }

        return items;
    }

    private async Task<IEnumerable<AutocompleteItem>> GetKeywordSpecificSuggestions(
        string address, string keyword, string basePath, string prefix, CancellationToken ct)
    {
        var items = new List<AutocompleteItem>();
        var searchPath = string.IsNullOrEmpty(basePath) ? address : $"{address}/{basePath}";

        if (meshQuery != null)
        {
            try
            {
                await foreach (var suggestion in meshQuery.AutocompleteAsync(searchPath, prefix, 15, ct))
                {
                    var insertPath = string.IsNullOrEmpty(basePath)
                        ? $"@{address}/{keyword}/{suggestion.Name}"
                        : $"@{address}/{keyword}/{basePath}/{suggestion.Name}";

                    // Add trailing space for completed items, slash for containers
                    var isContainer = suggestion.NodeType != null &&
                        (suggestion.NodeType.Contains("Collection") || suggestion.NodeType.Contains("Folder"));
                    insertPath += isContainer ? "/" : " ";

                    items.Add(new AutocompleteItem(
                        Label: suggestion.Name,
                        InsertText: insertPath,
                        Description: suggestion.NodeType ?? GetKeywordItemDescription(keyword),
                        Category: GetKeywordCategory(keyword),
                        Priority: ItemPriority,
                        Kind: GetKeywordKind(keyword)
                    ));
                }
            }
            catch
            {
                // Ignore errors
            }
        }

        return items;
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
