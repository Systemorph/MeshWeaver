using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MeshWeaver.Data.Completion;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Completion;

/// <summary>
/// Orchestrates chat autocomplete across four provider groups:
/// 1) Current Node — IAutocompleteProvider instances from the node hub (content, data, layout areas)
/// 2) Partitions — top-level partition list when typing @/
/// 3) Partition Drill-Down — scoped autocomplete within a selected partition
/// 4) Global Fan-Out — search across all partitions (lowest priority)
///
/// Results stream via IAsyncEnumerable so fast local results arrive before remote ones.
/// </summary>
internal sealed class ChatCompletionOrchestrator(
    IMeshService meshService,
    IMessageHub hub,
    ILogger<ChatCompletionOrchestrator>? logger = null)
    : IChatCompletionOrchestrator
{
    // Priority constants — higher values appear first in merged results
    private const int CurrentNodePriority = 3000;
    private const int PartitionDrillDownPriority = 2500;
    private const int PartitionListPriority = 2000;
    private const int GlobalFanOutPriority = 1000;

    // Score boost applied to current-node results so local "MyFile" always beats remote "MyFileOther"
    private const int CurrentNodeScoreBoost = 200;

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
                var tracker = new ProducerTracker(2, channel.Writer);
                _ = ProduceCurrentNodeAsync(reference, currentNamespace, channel.Writer, tracker, ct);
                _ = ProduceGlobalFanOutAsync("", reference, currentNamespace, channel.Writer, tracker, ct);
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

    private async Task ProduceCurrentNodeAsync(
        string reference,
        string? currentNamespace,
        ChannelWriter<CompletionBatch> writer,
        ProducerTracker tracker,
        CancellationToken ct)
    {
        try
        {
            var items = new List<AutocompleteItem>();

            // 1. Get IAutocompleteProvider instances from the current node's hub
            if (!string.IsNullOrEmpty(currentNamespace))
            {
                var nodeHub = hub.GetHostedHub(new Address(currentNamespace), HostedHubCreation.Never);
                if (nodeHub != null)
                {
                    var providers = nodeHub.ServiceProvider.GetServices<IAutocompleteProvider>();
                    foreach (var provider in providers)
                    {
                        try
                        {
                            await foreach (var item in provider.GetItemsAsync(
                                $"@{reference}", currentNamespace, ct))
                            {
                                // Boost priority and ensure absolute InsertText
                                var boosted = item with
                                {
                                    Priority = item.Priority + CurrentNodeScoreBoost,
                                    InsertText = EnsureAbsoluteInsertText(item.InsertText, currentNamespace)
                                };
                                items.Add(boosted);
                            }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            logger?.LogDebug(ex, "[ChatComplete] Provider {Provider} failed", provider.GetType().Name);
                        }
                    }
                }
            }

            // 2. Also get MeshNode children from the current namespace
            if (!string.IsNullOrEmpty(currentNamespace))
            {
                await foreach (var suggestion in meshService.AutocompleteAsync(
                    currentNamespace, reference, AutocompleteMode.RelevanceFirst, 20, currentNamespace, ct: ct))
                {
                    var item = SuggestionToItem(suggestion, CurrentNodePriority, "Nearby");
                    items.Add(item);
                }
            }

            // 3. Offer tag keywords scoped to current namespace
            if (!string.IsNullOrEmpty(currentNamespace))
                items.AddRange(GetTagKeywords(currentNamespace, reference));

            if (items.Count > 0)
                await writer.WriteAsync(new CompletionBatch("Nearby", CurrentNodePriority, items), ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[ChatComplete] CurrentNode producer failed");
        }
        finally
        {
            tracker.OnProducerDone();
        }
    }

    private async Task ProduceGlobalFanOutAsync(
        string basePath,
        string filter,
        string? currentNamespace,
        ChannelWriter<CompletionBatch> writer,
        ProducerTracker tracker,
        CancellationToken ct)
    {
        try
        {
            var items = new List<AutocompleteItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Exclude items already in the current namespace (they're in the "Nearby" batch)
            await foreach (var suggestion in meshService.AutocompleteAsync(
                basePath, filter, AutocompleteMode.RelevanceFirst, 20, currentNamespace, ct: ct))
            {
                // Skip items that belong to the current namespace (already in Nearby)
                if (!string.IsNullOrEmpty(currentNamespace) &&
                    suggestion.Path.StartsWith(currentNamespace + "/", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (seen.Add(suggestion.Path))
                    items.Add(SuggestionToItem(suggestion, GlobalFanOutPriority, "Global"));
            }

            if (items.Count > 0)
                await writer.WriteAsync(new CompletionBatch("Global", GlobalFanOutPriority, items), ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[ChatComplete] GlobalFanOut producer failed");
        }
        finally
        {
            tracker.OnProducerDone();
        }
    }

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

            // Get content/data providers from the node hub
            if (!string.IsNullOrEmpty(nodeAddress))
            {
                var nodeHub = hub.GetHostedHub(new Address(nodeAddress), HostedHubCreation.Never);
                if (nodeHub != null)
                {
                    var providers = nodeHub.ServiceProvider.GetServices<IAutocompleteProvider>();
                    foreach (var provider in providers)
                    {
                        try
                        {
                            await foreach (var item in provider.GetItemsAsync(
                                $"@{reference}", currentNamespace, ct))
                            {
                                var boosted = item with
                                {
                                    InsertText = EnsureAbsoluteInsertText(item.InsertText, nodeAddress)
                                };
                                items.Add(boosted);
                            }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            logger?.LogDebug(ex, "[ChatComplete] Tag provider {Provider} failed", provider.GetType().Name);
                        }
                    }
                }
            }

            if (items.Count > 0)
                await writer.WriteAsync(new CompletionBatch("Content", CurrentNodePriority, items), ct);
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

    #region Helpers

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
