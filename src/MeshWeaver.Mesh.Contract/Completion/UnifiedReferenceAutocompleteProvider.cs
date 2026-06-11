using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data.Completion;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
///
/// SNAPSHOT contract (see <see cref="IAutocompleteProvider"/>): every branch returns an
/// <c>IObservable&lt;IReadOnlyCollection&lt;AutocompleteItem&gt;&gt;</c>. Each sub-source (keywords,
/// children via <c>meshQuery.Autocomplete</c>, the per-node delegation) is itself a snapshot stream;
/// they are composed with <see cref="AutocompleteSnapshots.Combine"/> (CombineLatest + score-merge),
/// so the merged snapshot appears as soon as the FIRST source returns and refines as the rest arrive
/// — it never waits for the slowest. Empty results emit <see cref="AutocompleteSnapshots.Empty"/>,
/// never <c>Observable.Empty</c> (which would stall the aggregator's CombineLatest).
/// </summary>
internal class UnifiedReferenceAutocompleteProvider(
    IMeshService? meshQuery,
    INavigationService? navigationContext,
    IMessageHub hub,
    IAutocompletePrefixRegistry? prefixRegistry = null) : IAutocompleteProvider
{
    private const int ContextPriority = 2000;
    private const int PrefixPriority = 1800;
    private const int KeywordPriority = 1500;
    private const int ItemPriority = 1000;

    // Per-branch merge cap — generous (display is ≤ ~15); the outer per-node aggregator caps again.
    private const int CombineCap = 50;

    private static readonly IReadOnlyList<QueryResult> EmptyRows = Array.Empty<QueryResult>();

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
    public IObservable<IReadOnlyCollection<AutocompleteItem>> GetItems(string query, string? contextPath = null)
    {
        if (string.IsNullOrEmpty(query) || !query.StartsWith("@"))
            return Observable.Return(AutocompleteSnapshots.Empty);

        // Strip the @ prefix(es) - handle both @ and @@
        var path = query.TrimStart('@');

        // If the path starts with (or contains) a UCR prefix segment (content/, data/, schema/, etc.),
        // skip — dedicated providers (ContentAutocompleteProvider, DataAutocompleteProvider) handle these.
        if (StartsWithUcrPrefix(path))
            return Observable.Return(AutocompleteSnapshots.Empty);

        // Determine effective context: prefer explicit contextPath, fall back to navigation context
        var effectiveContext = contextPath ?? navigationContext?.CurrentNamespace;

        // Absolute mode: @/ means search globally
        if (path.StartsWith("/"))
            return GetAbsoluteSuggestions(path[1..]); // strip leading /

        // If we have context, use relative path mode
        if (!string.IsNullOrEmpty(effectiveContext))
            return GetRelativeSuggestions(path, effectiveContext);

        // No context — fall back to global absolute mode
        return GetAbsoluteSuggestions(path);
    }

    /// <summary>
    /// Returns true if the path starts with a UCR prefix segment (content/, data/, schema/, etc.)
    /// or is exactly a UCR prefix name. These are handled by dedicated providers.
    /// Uses the injected IAutocompletePrefixRegistry, which aggregates from all registered providers.
    /// </summary>
    private bool StartsWithUcrPrefix(string path)
    {
        if (string.IsNullOrEmpty(path) || prefixRegistry == null) return false;

        // Strip leading / for absolute paths
        var p = path.StartsWith("/") ? path[1..] : path;

        var firstSlash = p.IndexOf('/');
        var firstSegment = firstSlash > 0 ? p[..firstSlash] : p;

        return prefixRegistry.IsRegistered(firstSegment);
    }

    /// <summary>
    /// Maps each <see cref="IMeshService.Autocomplete"/> snapshot (a score-sorted set of
    /// <see cref="QueryResult"/>) directly to an <see cref="AutocompleteItem"/> snapshot — keeping the
    /// progressive nature (one snapshot per provider advance) without flattening to items. Errors
    /// collapse to an empty snapshot so a failing child can't stall the composing CombineLatest.
    /// </summary>
    private IObservable<IReadOnlyCollection<AutocompleteItem>> SnapshotItems(
        IObservable<IReadOnlyCollection<QueryResult>> source,
        Func<QueryResult, AutocompleteItem> toItem)
        => source
            .Select(rows => (IReadOnlyCollection<AutocompleteItem>)rows.Select(toItem).ToList())
            // Collapse to empty so a failing child can't stall the composing
            // CombineLatest — but keep the fault greppable (Debug: autocomplete
            // is high-frequency; a down backend would otherwise spam Warning).
            .Catch((Exception ex) =>
            {
                LogBranchFault(ex);
                return Observable.Return(AutocompleteSnapshots.Empty);
            });

    private void LogBranchFault(Exception ex)
        => hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(UnifiedReferenceAutocompleteProvider))
            .LogDebug(ex, "Autocomplete branch faulted — returning empty snapshot");

    /// <summary>
    /// Provides suggestions using relative paths from the current node context.
    /// Handles: "@child", "@../sibling", "@../../ancestor/child"
    /// </summary>
    private IObservable<IReadOnlyCollection<AutocompleteItem>> GetRelativeSuggestions(string path, string contextPath)
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
            return Observable.Return(AutocompleteSnapshots.Empty);

        // Keyword-specific suggestions (data:, content:, etc.)
        var lastSegment = completedSegments.LastOrDefault()?.ToLowerInvariant();
        if (lastSegment != null && Keywords.ContainsKey(lastSegment + "/"))
            return GetRelativeKeywordSuggestions(searchBase, lastSegment, relativePrefix, currentSegment);

        var streams = new List<IObservable<IReadOnlyCollection<AutocompleteItem>>>();

        // Suggest keywords if we ended with a slash and have at least one completed segment
        if (endsWithSlash && completedSegments.Length > 0)
            streams.Add(Observable.Return(
                (IReadOnlyCollection<AutocompleteItem>)GetRelativeKeywords(relativePrefix, currentSegment).ToList()));

        // Suggest child nodes at the searchBase
        streams.Add(SnapshotItems(
            meshQuery.Autocomplete(searchBase, currentSegment, AutocompleteMode.RelevanceFirst, 15),
            s => new AutocompleteItem(
                Label: s.Name ?? s.Path,
                InsertText: $"@{relativePrefix}{s.Name}/",
                Description: s.NodeType ?? "Node",
                Category: string.IsNullOrEmpty(relativePrefix) ? "Children" : "Nodes",
                Priority: ContextPriority,
                Kind: AutocompleteKind.Other)));

        // Node delegation: ask the node at searchBase for its own completions
        // (layout areas, data collections, content files)
        if (endsWithSlash && !string.IsNullOrEmpty(searchBase))
            streams.Add(GetNodeDelegatedCompletions(searchBase, relativePrefix, currentSegment));

        return AutocompleteSnapshots.Combine(streams, CombineCap);
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

    private IObservable<IReadOnlyCollection<AutocompleteItem>> GetRelativeKeywordSuggestions(
        string searchBase,
        string keyword,
        string relativePrefix,
        string currentSegment)
    {
        if (meshQuery == null)
            return Observable.Return(AutocompleteSnapshots.Empty);

        return SnapshotItems(
            meshQuery.Autocomplete(searchBase, currentSegment, AutocompleteMode.RelevanceFirst, 15),
            s => new AutocompleteItem(
                Label: s.Name ?? s.Path,
                InsertText: $"@{relativePrefix}{s.Name} ",
                Description: s.NodeType ?? GetKeywordItemDescription(keyword),
                Category: GetKeywordCategory(keyword),
                Priority: ItemPriority,
                Kind: GetKeywordKind(keyword)));
    }

    /// <summary>
    /// Provides suggestions using absolute paths (global search).
    /// Triggered by @/ or when no context is available.
    /// </summary>
    private IObservable<IReadOnlyCollection<AutocompleteItem>> GetAbsoluteSuggestions(string path)
    {
        var segments = path.Split('/', StringSplitOptions.None);
        var completedSegments = segments.SkipLast(1).ToArray();
        var currentSegment = segments.LastOrDefault() ?? "";
        var endsWithSlash = path.EndsWith("/");

        return GetAbsoluteSuggestionsForStage(completedSegments, currentSegment, endsWithSlash);
    }

    private IObservable<IReadOnlyCollection<AutocompleteItem>> GetAbsoluteSuggestionsForStage(
        string[] completedSegments,
        string currentSegment,
        bool endsWithSlash)
    {
        // Stage 1: No completed segments yet - suggest top-level addresses
        if (completedSegments.Length == 0)
            return GetTopLevelSuggestions(currentSegment);

        // Build the address from completed segments
        var address = string.Join("/", completedSegments);
        var streams = new List<IObservable<IReadOnlyCollection<AutocompleteItem>>>();

        // If we ended with a slash after at least 2 segments, suggest keywords + children
        if (completedSegments.Length >= 2 && endsWithSlash)
            streams.Add(Observable.Return(
                (IReadOnlyCollection<AutocompleteItem>)GetAbsoluteKeywordSuggestions(address, currentSegment).ToList()));

        // Check for keyword in segments — keyword-specific suggestions replace children + delegation
        if (completedSegments.Length >= 3)
        {
            var potentialKeyword = completedSegments.Last().ToLowerInvariant();
            if (Keywords.ContainsKey(potentialKeyword + "/"))
            {
                var keywordAddress = string.Join("/", completedSegments.SkipLast(1));
                streams.Add(GetAbsoluteKeywordSpecificSuggestions(keywordAddress, potentialKeyword, currentSegment));
                return AutocompleteSnapshots.Combine(streams, CombineCap);
            }
        }

        // Suggest children at current path
        if (meshQuery != null)
            streams.Add(SnapshotItems(
                meshQuery.Autocomplete(address, currentSegment, AutocompleteMode.RelevanceFirst, 15),
                s => new AutocompleteItem(
                    Label: $"{s.Name}/",
                    InsertText: $"@/{s.Path}/",
                    Description: s.NodeType ?? "Node",
                    Category: "Nodes",
                    Priority: ItemPriority,
                    Kind: AutocompleteKind.Other)));

        // Node delegation for absolute paths
        if (endsWithSlash && completedSegments.Length >= 1)
            streams.Add(GetNodeDelegatedCompletions(address, $"/{address}/", currentSegment));

        return AutocompleteSnapshots.Combine(streams, CombineCap);
    }

    private IObservable<IReadOnlyCollection<AutocompleteItem>> GetTopLevelSuggestions(string prefix)
    {
        var meshRows = meshQuery != null
            ? meshQuery.Autocomplete("", prefix, AutocompleteMode.RelevanceFirst, 15)
                .Catch((Exception ex) =>
                {
                    LogBranchFault(ex);
                    return Observable.Return(EmptyRows);
                })
            : Observable.Return(EmptyRows);

        // One snapshot per meshRows emission (progressive). Each snapshot is the FULL current set:
        // mesh-query top-level rows + static-node type definitions, deduped by path.
        return meshRows.Select(rows =>
        {
            var addedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var items = new List<AutocompleteItem>();

            // Top-level nodes from mesh query (root level)
            foreach (var suggestion in rows)
            {
                if (addedPaths.Add(suggestion.Path))
                    items.Add(new AutocompleteItem(
                        Label: $"{suggestion.Name}/",
                        InsertText: $"@/{suggestion.Path}/",
                        Description: suggestion.NodeType ?? suggestion.Name,
                        Category: "Addresses",
                        Priority: PrefixPriority,
                        Kind: AutocompleteKind.Other));
            }

            // Type definitions from static node providers
            var topLevelNodes = hub.ServiceProvider.EnumerateStaticNodes()
                .Where(n => n.Segments.Count == 1)
                .Where(n => string.IsNullOrEmpty(prefix) ||
                           n.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n.Order ?? int.MaxValue)
                .ThenBy(n => n.Name)
                .Take(10);

            foreach (var node in topLevelNodes)
            {
                if (addedPaths.Add(node.Path))
                    items.Add(new AutocompleteItem(
                        Label: $"{node.Path}/",
                        InsertText: $"@/{node.Path}/",
                        Description: node.Name,
                        Category: "Types",
                        Priority: PrefixPriority - (node.Order ?? 0),
                        Kind: AutocompleteKind.Other));
            }

            return (IReadOnlyCollection<AutocompleteItem>)items;
        });
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

    private IObservable<IReadOnlyCollection<AutocompleteItem>> GetAbsoluteKeywordSpecificSuggestions(
        string address,
        string keyword,
        string prefix)
    {
        if (meshQuery == null)
            return Observable.Return(AutocompleteSnapshots.Empty);

        return SnapshotItems(
            meshQuery.Autocomplete(address, prefix, AutocompleteMode.RelevanceFirst, 15),
            s => new AutocompleteItem(
                Label: s.Name ?? s.Path,
                InsertText: $"@/{address}/{keyword}/{s.Name} ",
                Description: s.NodeType ?? GetKeywordItemDescription(keyword),
                Category: GetKeywordCategory(keyword),
                Priority: ItemPriority,
                Kind: GetKeywordKind(keyword)));
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

    /// <summary>
    /// Asks the node at the given path for its own completions (layout areas, data collections,
    /// content files) by sending an AutocompleteRequest to that node's hub, and projects the single
    /// response into one snapshot. Fully observable — no <c>await</c>, no <c>.ToTask()</c>.
    /// See <c>Doc/Architecture/AsynchronousCalls.md</c>.
    /// </summary>
    private IObservable<IReadOnlyCollection<AutocompleteItem>> GetNodeDelegatedCompletions(
        string nodePath,
        string insertPrefix,
        string currentSegment)
    {
        return GetCompletionsViaHub(nodePath, currentSegment)
            .Select(response => (IReadOnlyCollection<AutocompleteItem>)
                (response?.Items ?? Enumerable.Empty<AutocompleteItem>())
                    .Select(item => item with { Priority = item.Priority > 0 ? item.Priority : ItemPriority })
                    .ToList());
    }

    /// <summary>2-second cap on a delegated per-node round-trip. Without this we
    /// inherit the framework's 30 s <c>RequestTimeout</c>, which means a hung /
    /// non-responding remote node hub stalls the entire autocomplete result for
    /// the whole 30 s.</summary>
    private static readonly TimeSpan NodeDelegationTimeout = TimeSpan.FromSeconds(2);

    private IObservable<AutocompleteResponse?> GetCompletionsViaHub(string nodePath, string currentSegment)
    {
        // Target the per-node hub at nodePath. Without an explicit target, the
        // request goes to the local hub which runs the AutocompleteRequest
        // aggregator — and the aggregator re-invokes every IAutocompleteProvider
        // including THIS one. Under any concurrent load that re-entrance
        // deadlocks the ActionBlock (the original handler is still pumping when
        // the delegated request arrives, action block has MaxDegreeOfParallelism=1).
        // The dispatch *must* land on a different hub.
        //
        // STREAM the response into the parent CombineLatest — NO FirstAsync. FirstAsync gates the
        // whole result on the node's single settled response: under load the response (and the
        // Timeout timer) lag, so any consumer that waits for completion stalls. Instead the parent's
        // per-source StartWith(empty) emits the overall snapshot immediately from the local sources
        // and folds this delegated result in WHEN it arrives. A slow/unreachable node degrades to a
        // null snapshot via the Timeout fallback observable (not an error, not a block).
        var request = new AutocompleteRequest($"@{currentSegment}", nodePath);
        return hub.Observe(request, o => o.WithTarget(new Address(nodePath)))
            .Select(d => d.Message as AutocompleteResponse)
            .Catch<AutocompleteResponse?, Exception>(_ => Observable.Return<AutocompleteResponse?>(null))
            .Timeout(NodeDelegationTimeout, Observable.Return<AutocompleteResponse?>(null));
    }
}
