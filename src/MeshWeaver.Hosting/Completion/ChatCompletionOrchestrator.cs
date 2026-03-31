using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MeshWeaver.Data.Completion;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Completion;

/// <summary>
/// Orchestrates chat autocomplete by blending three sources:
/// A) AutocompleteRequest via Post+RegisterCallback to the current node hub (custom providers)
/// B) IMeshService.QueryAsync with path:current scope:subtree (subtree search)
/// C) Adaptive broadening — partition-level then cross-partition when results are sparse
///
/// Additional modes for partition list, drill-down, and tag queries remain unchanged.
/// Results stream via IAsyncEnumerable so fast local results arrive before remote ones.
/// </summary>
internal sealed class ChatCompletionOrchestrator(
    IMeshService meshService,
    IMessageHub hub,
    ILogger<ChatCompletionOrchestrator>? logger = null)
    : IChatCompletionOrchestrator
{
    // Priority constants — higher values appear first in merged results
    private const int AutocompleteRequestPriority = 3000;  // Source A — local hub providers
    private const int SubtreeQueryPriority = 2800;          // Source B — subtree search
    private const int PartitionDrillDownPriority = 2500;
    private const int PartitionListPriority = 2000;
    private const int PartitionSearchPriority = 2000;       // Source C phase 1
    private const int CrossPartitionPriority = 1000;        // Source C phase 2

    // Score boost applied to Source A results so local items always beat remote ones
    private const int LocalProviderBoost = 200;

    // Minimum results before adaptive broadening kicks in
    private const int BroadeningThreshold = 10;

    /// <summary>Tracks how many producers have completed; closes the channel when all done.</summary>
    private sealed class ProducerTracker(int total, ChannelWriter<CompletionBatch> writer)
    {
        private int _completed;
        public void OnProducerDone()
        {
            if (Interlocked.Increment(ref _completed) >= total)
                writer.TryComplete();
        }
    }

    /// <summary>
    /// Tracks result counts from primary sources (A+B) so Source C can decide
    /// whether broadening is needed.
    /// </summary>
    private sealed class ResultCounter(int primaryCount)
    {
        private int _count;
        private int _primaryRemaining = primaryCount;
        private readonly TaskCompletionSource _primaryDone = new();

        public void Add(int count) => Interlocked.Add(ref _count, count);
        public int Count => Volatile.Read(ref _count);

        public void SignalPrimaryDone()
        {
            if (Interlocked.Decrement(ref _primaryRemaining) <= 0)
                _primaryDone.TrySetResult();
        }

        public Task WaitForPrimaryAsync(CancellationToken ct) => _primaryDone.Task.WaitAsync(ct);
    }

    public async IAsyncEnumerable<CompletionBatch> GetCompletionsAsync(
        string query,
        string? currentNamespace,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || !query.StartsWith("@"))
            yield break;

        var reference = query[1..]; // strip @

        var mode = ParseMode(reference);
        logger?.LogDebug("[ChatComplete] query={Query}, mode={Mode}, currentNamespace={NS}",
            query, mode.Mode, currentNamespace);

        var channel = Channel.CreateUnbounded<CompletionBatch>(
            new UnboundedChannelOptions { SingleReader = true });

        // Launch producers based on mode
        switch (mode.Mode)
        {
            case CompletionMode.PartitionList:
            {
                var tracker = new ProducerTracker(1, channel.Writer);
                _ = ProducePartitionListAsync(mode.Filter, channel.Writer, tracker, ct);
                break;
            }

            case CompletionMode.PartitionDrillDown:
            {
                var tracker = new ProducerTracker(1, channel.Writer);
                _ = ProducePartitionDrillDownAsync(mode.Partition!, mode.Filter, channel.Writer, tracker, ct);
                break;
            }

            case CompletionMode.CurrentNodeAndGlobal:
            {
                var seenPaths = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
                var resultCounter = new ResultCounter(2); // A + B are primary
                var tracker = new ProducerTracker(3, channel.Writer);
                _ = ProduceViaAutocompleteRequestAsync(reference, currentNamespace, seenPaths, resultCounter, channel.Writer, tracker, ct);
                _ = ProduceViaSubtreeQueryAsync(reference, currentNamespace, seenPaths, resultCounter, channel.Writer, tracker, ct);
                _ = ProduceViaBroadeningAsync(reference, currentNamespace, seenPaths, resultCounter, channel.Writer, tracker, ct);
                break;
            }

            case CompletionMode.TagQuery:
            {
                var tracker = new ProducerTracker(1, channel.Writer);
                _ = ProduceTagCompletionsAsync(reference, currentNamespace, channel.Writer, tracker, ct);
                break;
            }
        }

        await foreach (var batch in channel.Reader.ReadAllAsync(CancellationToken.None))
        {
            yield return batch;
        }
    }

    #region PartitionList & PartitionDrillDown (unchanged)

    private async Task ProducePartitionListAsync(
        string filter,
        ChannelWriter<CompletionBatch> writer,
        ProducerTracker tracker,
        CancellationToken ct)
    {
        try
        {
            var items = new List<AutocompleteItem>();

            // Get top-level partition suggestions via MeshService autocomplete
            await foreach (var suggestion in meshService.AutocompleteAsync("", filter, 30, ct))
            {
                items.Add(SuggestionToItem(suggestion, PartitionListPriority, "Partitions", isPartition: true));
            }

            if (items.Count > 0)
                await writer.WriteAsync(new CompletionBatch("Partitions", PartitionListPriority, items), ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[ChatComplete] PartitionList producer failed");
        }
        finally
        {
            tracker.OnProducerDone();
        }
    }

    private async Task ProducePartitionDrillDownAsync(
        string partition,
        string filter,
        ChannelWriter<CompletionBatch> writer,
        ProducerTracker tracker,
        CancellationToken ct)
    {
        try
        {
            var items = new List<AutocompleteItem>();

            await foreach (var suggestion in meshService.AutocompleteAsync(partition, filter, 20, ct))
            {
                items.Add(SuggestionToItem(suggestion, PartitionDrillDownPriority, "Items"));
            }

            // Also offer tag keywords (content:, data:, etc.)
            items.AddRange(GetTagKeywords(partition, filter));

            if (items.Count > 0)
                await writer.WriteAsync(new CompletionBatch("Items", PartitionDrillDownPriority, items), ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[ChatComplete] PartitionDrillDown producer failed for {Partition}", partition);
        }
        finally
        {
            tracker.OnProducerDone();
        }
    }

    #endregion

    #region Source A: AutocompleteRequest via messaging

    /// <summary>
    /// Sends AutocompleteRequest to the current node's hub address via Post+RegisterCallback.
    /// This queries custom IAutocompleteProvider instances registered on that hub without
    /// requiring the hub to be hosted locally.
    /// </summary>
    private async Task ProduceViaAutocompleteRequestAsync(
        string reference,
        string? currentNamespace,
        ConcurrentDictionary<string, byte> seenPaths,
        ResultCounter resultCounter,
        ChannelWriter<CompletionBatch> writer,
        ProducerTracker tracker,
        CancellationToken ct)
    {
        try
        {
            var items = new List<AutocompleteItem>();

            if (!string.IsNullOrEmpty(currentNamespace))
            {
                var response = await SendAutocompleteRequestAsync(
                    $"@{reference}", currentNamespace, new Address(currentNamespace), ct);

                if (response?.Items != null)
                {
                    foreach (var item in response.Items)
                    {
                        var boosted = item with
                        {
                            Priority = item.Priority + LocalProviderBoost,
                            InsertText = EnsureAbsoluteInsertText(item.InsertText, currentNamespace)
                        };
                        var key = boosted.Path ?? boosted.InsertText;
                        if (!string.IsNullOrEmpty(key) && seenPaths.TryAdd(key, 0))
                            items.Add(boosted);
                    }
                }

                // Offer tag keywords scoped to current namespace
                items.AddRange(GetTagKeywords(currentNamespace, reference));
            }

            resultCounter.Add(items.Count);

            if (items.Count > 0)
                await writer.WriteAsync(new CompletionBatch("Nearby", AutocompleteRequestPriority, items), ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[ChatComplete] AutocompleteRequest producer failed");
        }
        finally
        {
            resultCounter.SignalPrimaryDone();
            tracker.OnProducerDone();
        }
    }

    #endregion

    #region Source B: Subtree query via IMeshService.QueryAsync

    /// <summary>
    /// Searches the subtree under currentNamespace using the standard query infrastructure.
    /// Query: path:{currentNamespace} scope:subtree {searchText} is:main limit:20
    /// </summary>
    private async Task ProduceViaSubtreeQueryAsync(
        string reference,
        string? currentNamespace,
        ConcurrentDictionary<string, byte> seenPaths,
        ResultCounter resultCounter,
        ChannelWriter<CompletionBatch> writer,
        ProducerTracker tracker,
        CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrEmpty(currentNamespace) || string.IsNullOrEmpty(reference))
                return;

            var items = new List<AutocompleteItem>();
            var queryStr = $"path:{currentNamespace} scope:subtree {reference} is:main limit:20";
            var request = new MeshQueryRequest
            {
                Query = queryStr,
                ContextPath = currentNamespace,
                Limit = 20
            };

            await foreach (var result in meshService.QueryAsync(request, ct))
            {
                if (result is MeshNode node && !string.IsNullOrEmpty(node.Path))
                {
                    if (seenPaths.TryAdd(node.Path, 0))
                    {
                        items.Add(new AutocompleteItem(
                            Label: node.Name ?? node.Id,
                            InsertText: $"@/{node.Path}",
                            Description: node.NodeType,
                            Category: "Subtree",
                            Priority: SubtreeQueryPriority,
                            Kind: AutocompleteKind.File,
                            Icon: node.Icon,
                            Path: node.Path));
                    }
                }
            }

            resultCounter.Add(items.Count);

            if (items.Count > 0)
                await writer.WriteAsync(new CompletionBatch("Subtree", SubtreeQueryPriority, items), ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[ChatComplete] SubtreeQuery producer failed");
        }
        finally
        {
            resultCounter.SignalPrimaryDone();
            tracker.OnProducerDone();
        }
    }

    #endregion

    #region Source C: Adaptive broadening

    /// <summary>
    /// Waits for Sources A+B to complete, then broadens the search if results are sparse.
    /// Phase 1: Search within the current partition.
    /// Phase 2: Cross-partition search (global fan-out).
    /// Forced broadening when reference starts with ../ or /.
    /// </summary>
    private async Task ProduceViaBroadeningAsync(
        string reference,
        string? currentNamespace,
        ConcurrentDictionary<string, byte> seenPaths,
        ResultCounter resultCounter,
        ChannelWriter<CompletionBatch> writer,
        ProducerTracker tracker,
        CancellationToken ct)
    {
        try
        {
            var forcesBroadening = reference.StartsWith("../") || reference.StartsWith("/");
            var searchText = reference.TrimStart('.', '/');

            if (!forcesBroadening)
            {
                // Wait for primary sources (A+B) to finish, with a timeout
                try
                {
                    await resultCounter.WaitForPrimaryAsync(ct).WaitAsync(TimeSpan.FromSeconds(3), ct);
                }
                catch (TimeoutException) { /* proceed with broadening check */ }

                if (resultCounter.Count >= BroadeningThreshold)
                    return; // enough results, no broadening needed
            }

            // Phase 1: Search within the current partition
            var partition = ExtractPartition(currentNamespace);
            if (!string.IsNullOrEmpty(partition) && partition != currentNamespace)
            {
                var partitionItems = new List<AutocompleteItem>();

                await foreach (var suggestion in meshService.AutocompleteAsync(
                    partition, searchText, AutocompleteMode.RelevanceFirst, 20, currentNamespace, ct: ct))
                {
                    if (seenPaths.TryAdd(suggestion.Path, 0))
                    {
                        partitionItems.Add(SuggestionToItem(suggestion, PartitionSearchPriority, "Partition"));
                    }
                }

                resultCounter.Add(partitionItems.Count);

                if (partitionItems.Count > 0)
                    await writer.WriteAsync(new CompletionBatch("Partition", PartitionSearchPriority, partitionItems), ct);

                // If partition results + previous results are enough, stop
                if (resultCounter.Count >= BroadeningThreshold && !forcesBroadening)
                    return;
            }

            // Phase 2: Cross-partition (global fan-out)
            var globalItems = new List<AutocompleteItem>();

            await foreach (var suggestion in meshService.AutocompleteAsync(
                "", searchText, AutocompleteMode.RelevanceFirst, 20, currentNamespace, ct: ct))
            {
                if (seenPaths.TryAdd(suggestion.Path, 0))
                {
                    globalItems.Add(SuggestionToItem(suggestion, CrossPartitionPriority, "Global"));
                }
            }

            if (globalItems.Count > 0)
                await writer.WriteAsync(new CompletionBatch("Global", CrossPartitionPriority, globalItems), ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[ChatComplete] Broadening producer failed");
        }
        finally
        {
            tracker.OnProducerDone();
        }
    }

    #endregion

    #region TagQuery

    /// <summary>
    /// Handles tag queries (e.g., @path/content:readme) by sending AutocompleteRequest
    /// to the resolved node hub address via Post+RegisterCallback.
    /// </summary>
    private async Task ProduceTagCompletionsAsync(
        string reference,
        string? currentNamespace,
        ChannelWriter<CompletionBatch> writer,
        ProducerTracker tracker,
        CancellationToken ct)
    {
        try
        {
            var items = new List<AutocompleteItem>();

            // Resolve the node hub for the address portion before the colon tag
            var colonIndex = reference.IndexOf(':');
            if (colonIndex < 0)
                return;

            var addressPart = reference[..colonIndex];

            // The address may be nested: "Org/Sub/content" → nodeAddress="Org/Sub", tag="content"
            var lastSlash = addressPart.LastIndexOf('/');
            var nodeAddress = lastSlash >= 0 ? addressPart[..lastSlash] : currentNamespace ?? "";

            if (!string.IsNullOrEmpty(nodeAddress))
            {
                var response = await SendAutocompleteRequestAsync(
                    $"@{reference}", currentNamespace, new Address(nodeAddress), ct);

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
            }

            if (items.Count > 0)
                await writer.WriteAsync(new CompletionBatch("Content", AutocompleteRequestPriority, items), ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[ChatComplete] TagCompletions producer failed");
        }
        finally
        {
            tracker.OnProducerDone();
        }
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Sends an AutocompleteRequest to the given address via Post+RegisterCallback
    /// and awaits the response with a 2-second timeout.
    /// </summary>
    private async Task<AutocompleteResponse?> SendAutocompleteRequestAsync(
        string query, string? context, Address target, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<AutocompleteResponse?>();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));

        try
        {
            var request = new AutocompleteRequest(query, context);
            var delivery = hub.Post(request, o => o.WithTarget(target));

            if (delivery != null)
            {
                _ = hub.RegisterCallback((IMessageDelivery)delivery, (d, _) =>
                {
                    if (d is IMessageDelivery<AutocompleteResponse> resp)
                        tcs.TrySetResult(resp.Message);
                    else
                        tcs.TrySetResult(null);
                    return Task.FromResult(d);
                }, timeoutCts.Token);
            }
            else
            {
                tcs.TrySetResult(null);
            }

            return await tcs.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            tcs.TrySetResult(null);
            return null;
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "[ChatComplete] AutocompleteRequest to {Target} failed", target);
            tcs.TrySetResult(null);
            return null;
        }
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

        // Absolute reference: starts with /
        if (reference.StartsWith("/"))
        {
            var absolutePath = reference[1..]; // strip leading /

            if (string.IsNullOrEmpty(absolutePath))
            {
                // Just "@/" → partition list
                return new ParsedMode(CompletionMode.PartitionList, null, "");
            }

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

    private static AutocompleteItem SuggestionToItem(
        QuerySuggestion suggestion,
        int basePriority,
        string category,
        bool isPartition = false)
    {
        var label = suggestion.Name;
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

    private static string EnsureAbsoluteInsertText(string insertText, string currentNamespace)
    {
        if (string.IsNullOrEmpty(insertText))
            return insertText;

        // Already absolute
        if (insertText.StartsWith("@/"))
            return insertText;

        // Strip @ if present, then make absolute
        var path = insertText.StartsWith("@") ? insertText[1..] : insertText;

        // If the path doesn't start with the namespace, prepend it
        if (!string.IsNullOrEmpty(currentNamespace) &&
            !path.StartsWith(currentNamespace + "/", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith(currentNamespace + ":", StringComparison.OrdinalIgnoreCase) &&
            !path.Equals(currentNamespace, StringComparison.OrdinalIgnoreCase))
        {
            path = $"{currentNamespace}/{path}";
        }

        return $"@/{path}";
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
        ("content:", "Content files"),
        ("data:", "Data collections"),
        ("schema:", "JSON schemas"),
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
