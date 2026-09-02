using System.Collections.Concurrent;
using System.Reactive.Linq;
using MeshWeaver.Data.Completion;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Completion;

/// <summary>
/// Orchestrates chat autocomplete by blending multiple sources:
/// <list type="bullet">
///   <item>Source A — AutocompleteRequest via hub Post+Observe to the current node hub (custom providers)</item>
///   <item>Source B — IMeshService.Query with <c>path:current scope:subtree</c> (subtree search)</item>
///   <item>Source C — Adaptive broadening: partition-level fan-out when primary results are sparse</item>
///   <item>Partition list — when typing <c>@/</c> or <c>@/&lt;filter&gt;</c></item>
///   <item>Partition drill-down — when typing <c>@/Partition/</c></item>
///   <item>Tag query — when reference matches a UCR prefix (content/, data/, schema/, ...)</item>
/// </list>
/// All composition is reactive: each producer is an <see cref="IObservable{CompletionBatch}"/>
/// emitting at most one batch and then completing. The merged stream's <c>OnCompleted</c>
/// fires when every producer has finished, which the chat UI uses to hide its loading spinner.
/// </summary>
internal sealed class ChatCompletionOrchestrator(
    IMeshService meshService,
    IMessageHub hub,
    ILogger<ChatCompletionOrchestrator>? logger = null)
    : IChatCompletionOrchestrator
{
    // Priority constants — higher values appear first in merged results.
    private const int AutocompleteRequestPriority = 3000;  // Source A — local hub providers
    private const int SubtreeQueryPriority = 2800;          // Source B — subtree search
    private const int PartitionDrillDownPriority = 2500;
    private const int PartitionListPriority = 2000;
    private const int PartitionSearchPriority = 2000;       // Source C phase 1

    // Score boost so Source A's local items always beat remote ones.
    private const int LocalProviderBoost = 200;

    // Unified @-ranking (user spec 2026-06-28: "occurrence in path must weigh the most, title second"):
    // the blended sources (A current-node providers, B local subtree, C broadening) all emit under one
    // category band so the per-item AutocompleteRelevance.Score (path > title > relevance, 0..9990) is the
    // SOLE sort key the chat pipeline ranks on — see AutocompleteRelevance + ThreadChatView's sort key
    // (9999 - min(categoryPriority + Priority, 9999)). The relevance tier is the lowest-order tiebreaker,
    // and keeps "local first" (point 1): A/B outrank C only when path+title tiers tie.
    private const int BlendedBatchPriority = 0;
    private const int LocalProviderRelevanceRank = 8;  // Source A — providers on the current node hub
    private const int SubtreeRelevanceRankTop = 9;     // Source B — top vector/subtree hit (decreases by rank)
    private const int BroadeningRelevanceRank = 2;     // Source C — cross-namespace broadening

    // Once primary sources (A+B) yield this many items, broadening is skipped.
    private const int BroadeningThreshold = 10;

    // Inactivity timeout per producer — bounds the overall stream so OnCompleted
    // fires even if a backend hangs (chat input then hides its spinner). The
    // window is generous because cold-start fan-out across partitions can be
    // slow (initial schema provisioning, JSON deserialization). Per-call hub
    // timeouts (SendAutocompleteRequest = AutocompleteBounds.CallerBound) handle
    // the fast paths.
    private static readonly TimeSpan ProducerInactivityTimeout = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public IObservable<CompletionBatch> GetCompletions(string query, string? currentNamespace)
    {
        if (string.IsNullOrWhiteSpace(query) || !query.StartsWith("@"))
            return Observable.Empty<CompletionBatch>();

        var reference = query[1..]; // strip @

        var mode = ParseMode(reference);
        logger?.LogDebug("[ChatComplete] query={Query}, mode={Mode}, currentNamespace={NS}",
            query, mode.Mode, currentNamespace);

        var produced = mode.Mode switch
        {
            CompletionMode.PartitionList => ProducePartitionList(mode.Filter),
            CompletionMode.PartitionDrillDown => ProducePartitionDrillDown(mode.Partition!, mode.Filter),
            CompletionMode.TagQuery => ProduceTagCompletions(reference, currentNamespace),
            CompletionMode.CurrentNodeAndGlobal => ProduceCurrentAndGlobal(reference, currentNamespace),
            _ => Observable.Empty<CompletionBatch>()
        };

        // The caller's presentation screen (#1803) is applied to EVERY producer at this one seam —
        // a completion list is a name list, and it is the surface most likely to be on screen while
        // someone types during a demo. Display-only: nothing here changes a query, a permission or a
        // node, and every filtered item is still reachable by typing its full path.
        //
        // 🚨 Resolved HERE, synchronously on the caller's turn, and captured as a value. Reading the
        // ambient AccessContext from inside a producer's projection would land after a scheduler hop
        // with the AsyncLocal gone — resolving to "nobody", whose screen hides nothing. That is the
        // silent-failure shape, not a hypothetical.
        return ApplyPresentationScreen(produced);
    }

    /// <summary>
    /// Filters every batch through the caller's presentation screen and drops batches the screen
    /// left empty (a heading with nothing under it would itself say that something was removed).
    ///
    /// <para>🚨 The fast-path asks <see cref="PresentationScreen.HidesAnything"/>, NOT
    /// <c>== PresentationScreen.Off</c>. A viewer who has marked things but currently has the mode
    /// OFF holds a screen that is not <c>Off</c> and yet hides nothing — reference equality would
    /// send them down the filtering branch, where <c>Filter</c> is correctly a no-op but the
    /// empty-batch drop is NOT, silently suppressing a legitimately empty category for someone who
    /// is not presenting at all. Whether a screen can hide anything is the question; ask it.</para>
    /// </summary>
    private IObservable<CompletionBatch> ApplyPresentationScreen(IObservable<CompletionBatch> batches)
    {
        var screen = hub.ServiceProvider.GetService<AccessService>().ViewerScreen(hub).Take(1);
        return screen.SelectMany(viewerScreen => Screened(batches, viewerScreen));
    }

    /// <summary>
    /// The projection itself, as a pure observable-in / observable-out function of the screen —
    /// separated from the identity resolution above so the "hides nothing" leg is directly
    /// executable by a test, with no hub and no AccessContext. That leg is exactly the one that was
    /// wrong, and a test that could only assert the PREDICATE would not have caught it.
    /// </summary>
    /// <param name="batches">The producers' batches.</param>
    /// <param name="screen">The caller's presentation screen.</param>
    internal static IObservable<CompletionBatch> Screened(
        IObservable<CompletionBatch> batches, PresentationScreen screen)
        => !screen.HidesAnything
            // Nothing to hide — mode off, or on with nothing marked. Pass the batches through
            // untouched, byte for byte, so a non-presenting viewer's completions are unaffected.
            ? batches
            : batches
                .Select(batch => batch with
                {
                    Items = screen.Filter(batch.Items, CompletionPath).ToList()
                })
                .Where(batch => batch.Items.Count > 0);

    /// <summary>
    /// The mesh path a completion stands for: its <see cref="AutocompleteItem.Path"/> when the
    /// producer set one, else the absolute <c>@Path/</c> insert text
    /// (<see cref="EnsureAbsoluteInsertText"/> makes source A's items absolute before they get
    /// here). Null for a completion that is not a node at all — a tag keyword, a command — which
    /// the screen then keeps, correctly: it hides paths, not vocabulary.
    /// </summary>
    internal static string? CompletionPath(AutocompleteItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Path))
            return item.Path;
        var insert = item.InsertText;
        if (string.IsNullOrEmpty(insert) || insert[0] != '@')
            return null;
        var path = insert[1..].TrimEnd('/');
        // "@/Partition/…" is the partition-scoped form of the same absolute path.
        return path.Length == 0 ? null : path;
    }

    #region PartitionList & PartitionDrillDown

    /// <summary>
    /// Lists partitions for <c>@/</c> or <c>@/&lt;filter&gt;</c>. Backed by a
    /// regular <c>Autocomplete</c> snapshot against
    /// <c>namespace:Admin/Partition</c> — each backend's <see cref="IMeshQueryProvider"/>
    /// answers from its own source of truth: Postgres queries
    /// <c>public.partitions</c>, in-memory / embedded / static return their
    /// in-process partition lists. No global enumeration, no centralized
    /// "load all partitions" cache.
    /// </summary>
    private IObservable<CompletionBatch> ProducePartitionList(string filter)
    {
        var request = new MeshQueryRequest
        {
            Query = "namespace:Admin/Partition nodeType:Partition",
            Limit = 100
        };
        return meshService.Query<MeshNode>(request)
            .Take(1)
            .Select(change =>
            {
                var matches = change.Items
                    .Select(n => n.Path?.Split('/').LastOrDefault())
                    .Where(seg => !string.IsNullOrEmpty(seg) && MatchesPartitionFilter(seg!, filter))
                    .OrderBy(seg => seg, StringComparer.OrdinalIgnoreCase)
                    .Select(seg => new AutocompleteItem(
                        Label: seg!,
                        InsertText: $"@/{seg}/",
                        Description: "Partition",
                        Category: "Partitions",
                        Priority: PartitionListPriority,
                        Kind: AutocompleteKind.Other,
                        Path: seg!))
                    .ToArray();
                return (IReadOnlyList<AutocompleteItem>)matches;
            })
            .Where(items => items.Count > 0)
            .Select(items => new CompletionBatch("Partitions", PartitionListPriority, items))
            .Timeout(ProducerInactivityTimeout)
            .Catch<CompletionBatch, Exception>(ex =>
            {
                if (ex is not (TimeoutException or OperationCanceledException))
                    logger?.LogWarning(ex, "[ChatComplete] PartitionList producer failed");
                return Observable.Empty<CompletionBatch>();
            });
    }

    private static bool MatchesPartitionFilter(string partitionKey, string filter)
    {
        if (string.IsNullOrEmpty(filter)) return true;
        return partitionKey.StartsWith(filter, StringComparison.OrdinalIgnoreCase)
            || partitionKey.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private IObservable<CompletionBatch> ProducePartitionDrillDown(string partition, string filter)
    {
        return meshService.Autocomplete(partition, filter, AutocompleteMode.PathFirst, 20)
            .Select(snapshot =>
            {
                var combined = snapshot
                    .Select(s => SuggestionToItem(s, PartitionDrillDownPriority, "Items"))
                    .ToList();
                combined.AddRange(GetTagKeywords(partition, filter));
                return (IReadOnlyList<AutocompleteItem>)combined;
            })
            .Where(items => items.Count > 0)
            .Select(items => new CompletionBatch("Items", PartitionDrillDownPriority, items))
            .Timeout(ProducerInactivityTimeout)
            .Catch<CompletionBatch, Exception>(ex =>
            {
                if (ex is not (TimeoutException or OperationCanceledException))
                    logger?.LogWarning(ex, "[ChatComplete] PartitionDrillDown producer failed for {Partition}", partition);
                return Observable.Empty<CompletionBatch>();
            });
    }

    #endregion

    #region CurrentNodeAndGlobal: Sources A + B + C

    /// <summary>
    /// Runs A and B in parallel (merge), then runs C only after both complete and only
    /// when results are sparse (or the reference forces broadening with <c>../</c> / <c>/</c>).
    /// <para>The reactive shape is <c>Merge(A, B).Concat(Defer(maybeC))</c>: <see cref="Observable.Concat{TSource}(IObservable{TSource}, IObservable{TSource})"/>
    /// subscribes to the deferred C only after the merged A+B stream completes, and the deferred
    /// closure reads the accumulated count to decide whether to actually run broadening.</para>
    /// </summary>
    private IObservable<CompletionBatch> ProduceCurrentAndGlobal(string reference, string? currentNamespace)
    {
        return Observable.Defer(() =>
        {
            var seenPaths = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            var totalCount = 0;

            var a = ProduceViaAutocompleteRequest(reference, currentNamespace, seenPaths)
                .Do(batch => Interlocked.Add(ref totalCount, batch.Items.Count));
            var b = ProduceViaSubtreeQuery(reference, currentNamespace, seenPaths)
                .Do(batch => Interlocked.Add(ref totalCount, batch.Items.Count));

            return Observable.Merge(a, b)
                .Concat(Observable.Defer(() =>
                {
                    var forcesBroadening = reference.StartsWith("../") || reference.StartsWith("/");
                    if (!forcesBroadening && totalCount >= BroadeningThreshold)
                        return Observable.Empty<CompletionBatch>();
                    return ProduceViaBroadening(reference, currentNamespace, seenPaths);
                }));
        });
    }

    /// <summary>
    /// Source A: AutocompleteRequest via hub Post+Observe to the current node hub.
    /// Queries custom <c>IAutocompleteProvider</c> instances registered on that hub.
    /// </summary>
    private IObservable<CompletionBatch> ProduceViaAutocompleteRequest(
        string reference,
        string? currentNamespace,
        ConcurrentDictionary<string, byte> seenPaths)
    {
        if (string.IsNullOrEmpty(currentNamespace))
            return Observable.Empty<CompletionBatch>();

        // Resolve to parent node for satellite contexts (threads, comments, activity).
        // Content collections, layout areas, data live on the parent node — not on satellites.
        var targetNamespace = ResolveParentNodeNamespace(currentNamespace);

        return SendAutocompleteRequest($"@{reference}", currentNamespace, new Address(targetNamespace))
            .Select(response =>
            {
                var items = new List<AutocompleteItem>();
                if (response?.Items != null)
                {
                    foreach (var item in response.Items)
                    {
                        var boosted = item with
                        {
                            Priority = AutocompleteRelevance.Score(
                                reference, item.Path, item.Label, LocalProviderRelevanceRank),
                            InsertText = EnsureAbsoluteInsertText(item.InsertText, currentNamespace)
                        };
                        var key = boosted.Path ?? boosted.InsertText;
                        if (!string.IsNullOrEmpty(key) && seenPaths.TryAdd(key, 0))
                            items.Add(boosted);
                    }
                }

                // Offer tag keywords scoped to current namespace.
                items.AddRange(GetTagKeywords(currentNamespace, reference));
                return (IReadOnlyList<AutocompleteItem>)items;
            })
            .Where(items => items.Count > 0)
            .Select(items => new CompletionBatch("Nearby", BlendedBatchPriority, items))
            .Catch<CompletionBatch, Exception>(ex =>
            {
                logger?.LogWarning(ex, "[ChatComplete] AutocompleteRequest producer failed");
                return Observable.Empty<CompletionBatch>();
            });
    }

    /// <summary>
    /// Source B: searches the subtree under <paramref name="currentNamespace"/> using the
    /// standard query infrastructure (<c>path:NS scope:subtree {ref} is:main limit:20</c>).
    /// </summary>
    private IObservable<CompletionBatch> ProduceViaSubtreeQuery(
        string reference,
        string? currentNamespace,
        ConcurrentDictionary<string, byte> seenPaths)
    {
        if (string.IsNullOrEmpty(currentNamespace) || string.IsNullOrEmpty(reference))
            return Observable.Empty<CompletionBatch>();

        var queryStr = $"path:{currentNamespace} scope:subtree {reference} is:main limit:20";
        var request = new MeshQueryRequest
        {
            Query = queryStr,
            ContextPath = currentNamespace,
            Limit = 20
        };

        return meshService.Query<MeshNode>(request)
            .Take(1)
            .SelectMany(c => c.Items.ToObservable())
            .Where(node => !string.IsNullOrEmpty(node.Path) && seenPaths.TryAdd(node.Path!, 0))
            // Results arrive in vector/subtree-relevance order; the position feeds the lowest-order
            // relevance tier so the (path > title > vector) ranking honours the index too.
            .Select((node, index) => new AutocompleteItem(
                Label: node.Name ?? node.Id,
                InsertText: $"@/{node.Path}",
                Description: node.NodeType,
                Category: "Subtree",
                Priority: AutocompleteRelevance.Score(
                    reference, node.Path, node.Name ?? node.Id,
                    Math.Max(0, SubtreeRelevanceRankTop - index)),
                Kind: AutocompleteKind.File,
                Icon: node.Icon,
                Path: node.Path))
            .ToArray()
            .Where(items => items.Length > 0)
            .Select(items => new CompletionBatch("Subtree", BlendedBatchPriority, items))
            .Timeout(ProducerInactivityTimeout)
            .Catch<CompletionBatch, Exception>(ex =>
            {
                if (ex is not (TimeoutException or OperationCanceledException))
                    logger?.LogWarning(ex, "[ChatComplete] SubtreeQuery producer failed");
                return Observable.Empty<CompletionBatch>();
            });
    }

    /// <summary>
    /// Source C: broadens the search to the current partition when A+B were sparse.
    /// Phase 2 (global cross-partition fan-out) is intentionally absent — non-/ queries
    /// stay within the current partition; use <c>@/</c> to search globally.
    /// </summary>
    private IObservable<CompletionBatch> ProduceViaBroadening(
        string reference,
        string? currentNamespace,
        ConcurrentDictionary<string, byte> seenPaths)
    {
        var partition = ExtractPartition(currentNamespace);
        if (string.IsNullOrEmpty(partition) || partition == currentNamespace)
            return Observable.Empty<CompletionBatch>();

        var searchText = reference.TrimStart('.', '/');

        return meshService.Autocomplete(
                partition, searchText, AutocompleteMode.RelevanceFirst, 20, currentNamespace)
            .Select(snapshot => snapshot
                .Where(s => seenPaths.TryAdd(s.Path, 0))
                .Select(s =>
                {
                    var item = SuggestionToItem(s, PartitionSearchPriority, "Partition");
                    return item with
                    {
                        Priority = AutocompleteRelevance.Score(
                            reference, item.Path, item.Label, BroadeningRelevanceRank)
                    };
                })
                .ToArray())
            .Where(items => items.Length > 0)
            .Select(items => new CompletionBatch("Partition", BlendedBatchPriority, items))
            .Timeout(ProducerInactivityTimeout)
            .Catch<CompletionBatch, Exception>(ex =>
            {
                if (ex is not (TimeoutException or OperationCanceledException))
                    logger?.LogWarning(ex, "[ChatComplete] Broadening producer failed");
                return Observable.Empty<CompletionBatch>();
            });
    }

    #endregion

    #region TagQuery

    /// <summary>
    /// Handles tag queries by sending AutocompleteRequest to the resolved node hub.
    /// Supports both colon format (<c>@path/content:readme</c>) and slash format
    /// (<c>@path/content/readme</c>, <c>@content/file</c>).
    /// </summary>
    private IObservable<CompletionBatch> ProduceTagCompletions(string reference, string? currentNamespace)
    {
        return Observable.Defer(() =>
        {
            // Find the node address by locating the UCR prefix segment.
            string? nodeAddress = null;

            // Try colon format first: "Org/Sub/content:readme" → nodeAddress="Org/Sub"
            var colonIndex = reference.IndexOf(':');
            if (colonIndex > 0)
            {
                var addressPart = reference[..colonIndex];
                var lastSlash = addressPart.LastIndexOf('/');
                nodeAddress = lastSlash >= 0 ? addressPart[..lastSlash] : currentNamespace;
            }
            else
            {
                // Slash format: find the UCR prefix segment.
                // "Org/Sub/content/readme" → nodeAddress="Org/Sub"
                // "content/readme" → nodeAddress=currentNamespace
                var segments = reference.TrimStart('/').Split('/');
                for (var i = 0; i < segments.Length; i++)
                {
                    if (Data.UcrPrefixResolver.PrefixToAreaMap.ContainsKey(segments[i]))
                    {
                        nodeAddress = i > 0 ? string.Join("/", segments.Take(i)) : currentNamespace;
                        break;
                    }
                }
            }

            nodeAddress ??= currentNamespace;
            // Resolve satellite contexts (threads, comments) to their parent node.
            nodeAddress = ResolveParentNodeNamespace(nodeAddress ?? string.Empty);

            if (string.IsNullOrEmpty(nodeAddress))
                return Observable.Empty<CompletionBatch>();

            return SendAutocompleteRequest($"@{reference}", currentNamespace, new Address(nodeAddress))
                .Select(response =>
                {
                    var items = new List<AutocompleteItem>();
                    if (response?.Items != null)
                    {
                        foreach (var item in response.Items)
                        {
                            var boosted = item with
                            {
                                InsertText = EnsureAbsoluteInsertText(item.InsertText, nodeAddress)
                            };
                            items.Add(boosted);
                        }
                    }
                    return (IReadOnlyList<AutocompleteItem>)items;
                })
                .Where(items => items.Count > 0)
                .Select(items => new CompletionBatch("Content", AutocompleteRequestPriority, items));
        })
        .Catch<CompletionBatch, Exception>(ex =>
        {
            logger?.LogWarning(ex, "[ChatComplete] TagCompletions producer failed");
            return Observable.Empty<CompletionBatch>();
        });
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Sends an AutocompleteRequest to the given address via Post+Observe and emits the
    /// response (or null) within <see cref="AutocompleteBounds.CallerBound"/>. Pure reactive — no
    /// Task, no async; the hub.Observe stream provides the response and Rx's Timeout enforces
    /// the deadline.
    ///
    /// <para>🚨 The bound is DERIVED from the producer's, never written here. It used to be a
    /// literal 2 s — exactly the handler's own answer deadline — so a response the handler
    /// legitimately took its full deadline to produce raced this timeout and was dropped as often
    /// as it was kept. Invisible while the handler answered on a 150 ms settle window; a coin toss
    /// the moment it is allowed to wait for every provider to settle (#3094).</para>
    /// </summary>
    private IObservable<AutocompleteResponse?> SendAutocompleteRequest(
        string query, string? context, Address target)
    {
        return Observable.Defer<AutocompleteResponse?>(() =>
        {
            var request = new AutocompleteRequest(query, context);
            var delivery = hub.Post(request, o => o.WithTarget(target));
            if (delivery == null)
                return Observable.Return<AutocompleteResponse?>(null);

            return hub.Observe((IMessageDelivery)delivery)
                .Take(1)
                .Select(d => d.Message as AutocompleteResponse);
        })
        .Timeout(AutocompleteBounds.CallerBound)
        .Catch<AutocompleteResponse?, Exception>(ex =>
        {
            if (ex is not (TimeoutException or OperationCanceledException))
                logger?.LogDebug(ex, "[ChatComplete] AutocompleteRequest to {Target} failed", target);
            return Observable.Return<AutocompleteResponse?>(null);
        });
    }

    private enum CompletionMode
    {
        PartitionList,        // @/ → show all partitions
        PartitionDrillDown,   // @/Partition/ or @/Partition/text → drill into partition
        CurrentNodeAndGlobal, // @text → current node + global fan-out
        TagQuery              // @path/tag:subpath → content/data tag completions
    }

    private record ParsedMode(CompletionMode Mode, string? Partition, string Filter);

    private static ParsedMode ParseMode(string reference)
    {
        // Tag query: contains a colon (e.g., "Org/content:readme")
        if (reference.Contains(':'))
            return new ParsedMode(CompletionMode.TagQuery, null, reference);

        // Tag query with / format: starts with or contains a known UCR prefix segment
        // e.g., "content/file.md" or "Org/content/file.md"
        if (IsUcrPrefixPath(reference))
            return new ParsedMode(CompletionMode.TagQuery, null, reference);

        // Absolute reference: starts with /
        if (reference.StartsWith("/"))
        {
            var absolutePath = reference[1..]; // strip leading /

            if (string.IsNullOrEmpty(absolutePath))
            {
                // Just "@/" → partition list
                return new ParsedMode(CompletionMode.PartitionList, null, "");
            }

            // Check if absolute path contains a UCR prefix (e.g., "/Org/content/file")
            if (IsUcrPrefixPath(absolutePath))
                return new ParsedMode(CompletionMode.TagQuery, null, reference);

            // Check if we have a completed partition segment: "Partition/" or "Partition/sub"
            var slashIndex = absolutePath.IndexOf('/');
            if (slashIndex >= 0)
            {
                var partition = absolutePath[..slashIndex];
                var rest = absolutePath[(slashIndex + 1)..];
                return new ParsedMode(CompletionMode.PartitionDrillDown, partition, rest);
            }

            // Partial partition name: "Parti" → filter partition list
            return new ParsedMode(CompletionMode.PartitionList, null, absolutePath);
        }

        // Regular: @text → current node + global
        return new ParsedMode(CompletionMode.CurrentNodeAndGlobal, null, reference);
    }

    /// <summary>
    /// Checks if a path contains a known UCR prefix segment (content, data, schema, model, menu).
    /// </summary>
    private static bool IsUcrPrefixPath(string path)
    {
        var segments = path.Split('/');
        return segments.Any(s => Data.UcrPrefixResolver.PrefixToAreaMap.ContainsKey(s));
    }

    private static AutocompleteItem SuggestionToItem(
        QueryResult suggestion,
        int basePriority,
        string category,
        bool isPartition = false)
    {
        var label = suggestion.Name ?? suggestion.Path;
        var insertText = isPartition
            ? $"@/{suggestion.Path}/"  // Partitions get trailing slash for drill-down
            : $"@/{suggestion.Path}";  // Other items get absolute path

        return new AutocompleteItem(
            Label: label,
            InsertText: insertText,
            Description: suggestion.NodeType,
            Category: category,
            Priority: basePriority + (int)suggestion.Score,
            Kind: isPartition ? AutocompleteKind.Other : AutocompleteKind.File,
            Icon: suggestion.Icon,
            Path: suggestion.Path);
    }

    /// <summary>
    /// Pass through the provider's insert text. Providers are expected to produce relative
    /// paths (e.g., "@content/file.md") when in context, since chat messages resolve
    /// references relative to the current chat context.
    /// </summary>
    private static string EnsureAbsoluteInsertText(string insertText, string currentNamespace)
        => insertText;

    /// <summary>
    /// For satellite contexts (threads, comments, activity), returns the parent node's namespace.
    /// E.g., "User/rbuergi/_Thread/abc123" → "User/rbuergi". Content collections, layout areas,
    /// and data live on the parent node, not on satellite sub-namespaces.
    /// Satellite segments start with underscore (e.g., _Thread, _Comment, _Activity).
    /// </summary>
    private static string ResolveParentNodeNamespace(string ns)
    {
        if (string.IsNullOrEmpty(ns)) return ns;

        var segments = ns.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            if (segments[i].StartsWith('_'))
                return string.Join("/", segments.Take(i));
        }
        return ns;
    }

    /// <summary>
    /// Extracts the partition (first path segment) from a namespace.
    /// "ACME/Marketing/Q1" → "ACME"
    /// </summary>
    private static string? ExtractPartition(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var slashIndex = path.IndexOf('/');
        return slashIndex >= 0 ? path[..slashIndex] : path;
    }

    private static readonly (string Tag, string Description)[] TagKeywords =
    [
        ("content/", "Content files"),
        ("data/", "Data collections"),
        ("schema/", "JSON schemas"),
    ];

    private static List<AutocompleteItem> GetTagKeywords(string address, string filter)
    {
        var items = new List<AutocompleteItem>();
        foreach (var (tag, description) in TagKeywords)
        {
            if (!string.IsNullOrEmpty(filter) &&
                !tag.StartsWith(filter, StringComparison.OrdinalIgnoreCase))
                continue;

            items.Add(new AutocompleteItem(
                Label: tag,
                InsertText: $"@/{address}/{tag}",
                Description: description,
                Category: "Keywords",
                Priority: 1500,
                Kind: AutocompleteKind.Other));
        }
        return items;
    }

    #endregion
}

