using System.ComponentModel;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using MeshWeaver.Data;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.AI.Plugins;

/// <summary>
/// Agent plugin for uploading text content (SVG, markdown, JSON, CSS, etc.)
/// to a node's content collection. Routes through the node hub which has
/// the IFileContentProvider registered.
/// </summary>

public class ContentCollectionPlugin(IMessageHub hub, IAgentChat chat) : IAgentPlugin
{
    public string Name => "ContentCollection";

    public IEnumerable<AITool> CreateTools()
    {
        return
        [
            AIFunctionFactory.Create(UploadContent),
            AIFunctionFactory.Create(SearchChunks),
            AIFunctionFactory.Create(GetChunk),
        ];
    }

    [Description(
        "Semantic search over INDEXED content chunks — the chunk-level companion to the node `Search`. " +
        "Where node search resolves a hit up to its Document node and drops the chunk position, this " +
        "returns the matching chunks themselves WITH their (collectionPath, filePath, chunkIndex) so you " +
        "can read the exact window or step through neighbours with get_chunk. Use this to FIND relevant " +
        "passages and gather context; use Get on the Document for whole-document reads (e.g. table " +
        "extraction). Returns {count, results:[{documentPath, collectionPath, filePath, chunkIndex, rank, snippet}]}.")]
    public Task<string> SearchChunks(
        [Description("Free-text query. Matched semantically against indexed chunk text (1000-char windows, 150-char overlap).")] string query,
        [Description("Node path to anchor the search at — this path AND each ancestor prefix are searched (e.g. 'ACME/Reports'). Optional: defaults to the agent's current context. If neither is set an empty result with a hint is returned.")] string? scope = null,
        [Description("Maximum number of chunk hits to return (1-200, default 20). Not deduped by file — chunk-level hits are the point.")] int limit = 20)
    {
        var scopePath = !string.IsNullOrWhiteSpace(scope)
            ? MeshOperations.ResolvePath(MeshOperations.ResolveContextPath(chat, scope))
            : chat.Context?.Context;

        return ChunkNavigation.SearchChunks(hub.ServiceProvider, query, scopePath, limit)
            .FirstAsync().ToTask();
    }

    [Description(
        "Reads ONE indexed content chunk by its 0-based index within a file, with prev/next links so you " +
        "can step through the file's chunk sequence. Use after search_chunks (which gives you the " +
        "collectionPath/filePath/chunkIndex of a hit) to read the full window and walk to adjacent chunks. " +
        "Returns {found, collectionPath, filePath, chunkIndex, text, prevIndex, nextIndex, totalChunks} — " +
        "prevIndex is null at index 0, nextIndex is null at the last chunk.")]
    public Task<string> GetChunk(
        [Description("The content collection path the chunk belongs to (the 'collectionPath' from a search_chunks hit).")] string collectionPath,
        [Description("The file path within the collection (the 'filePath' from a search_chunks hit).")] string filePath,
        [Description("0-based chunk index within the file (the 'chunkIndex' from a search_chunks hit, or a prevIndex/nextIndex to step).")] int chunkIndex)
        => ChunkNavigation.GetChunk(hub.ServiceProvider, collectionPath, filePath, chunkIndex)
            .FirstAsync().ToTask();

    [Description("Uploads text content (SVG, markdown, JSON, CSS, etc.) to a node's content collection. Use for storing diagrams, images (SVG), stylesheets, or any text-based files alongside a node.")]
    public Task<string> UploadContent(
        [Description("Canonical path to the node that owns the collection — use the MeshNode's `path` property, NOT its `name`. Use @/full/path for absolute or @relative/path relative to the current context. Example: @/PartnerRe/AIConsulting or @FinalReport. If you only know the display name, call Search('name:\"...\"') first and use the path field of the match.")] string nodePath,
        [Description("File name/path within the collection (e.g., 'diagram.svg', 'images/architecture.svg')")] string filePath,
        [Description("The text content to upload (SVG markup, markdown, JSON, etc.)")] string content,
        [Description("Collection name (default: 'content')")] string collectionName = "content",
        CancellationToken cancellationToken = default)
    {
        var resolvedPath = MeshOperations.ResolvePath(MeshOperations.ResolveContextPath(chat, nodePath));
        var address = new Address(resolvedPath);

        // Post + RegisterCallback + TCS — never `await hub.RegisterCallback(..., ct)` from
        // a plugin method: that blocks the hub scheduler. The callback fires on a non-hub
        // thread when the response arrives and resolves the TCS to unblock the caller.
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
        var registration = timeoutCts.Token.Register(() =>
        {
            if (cancellationToken.IsCancellationRequested)
                tcs.TrySetCanceled(cancellationToken);
            else
                tcs.TrySetResult($"Error uploading to {resolvedPath}: the operation timed out waiting for a response.");
        });
        _ = tcs.Task.ContinueWith(_ => { registration.Dispose(); timeoutCts.Dispose(); }, TaskScheduler.Default);

        var delivery = hub.Post(
            new SaveContentRequest
            {
                CollectionName = collectionName,
                FilePath = filePath,
                TextContent = content
            },
            o => o.WithTarget(address))!;

        hub.Observe(delivery)
            .Subscribe(
                callback =>
                {
                    if (callback.Message is SaveContentResponse typed)
                    {
                        tcs.TrySetResult(typed.Success
                            ? $"Uploaded `{filePath}` to @{resolvedPath}/{collectionName}/{filePath}"
                            : $"Error: {typed.Error}");
                    }
                    else
                    {
                        tcs.TrySetResult($"Error: unexpected response {callback.Message?.GetType().Name ?? "null"} uploading to {resolvedPath}.");
                    }
                },
                ex => tcs.TrySetResult(
                    $"Error uploading to {resolvedPath}: {ex.Message ?? "delivery failed"}. " +
                    $"Check that '{nodePath}' resolves to an existing node — pass the MeshNode's " +
                    "`path` property, not its `name`."));

        return tcs.Task;
    }
}

/// <summary>
/// Request to save text content to a node's content collection.
/// Handled by the node hub which has IFileContentProvider registered.
/// </summary>
public record SaveContentRequest : IRequest<SaveContentResponse>
{
    public required string CollectionName { get; init; }
    public required string FilePath { get; init; }
    public required string TextContent { get; init; }
}

public record SaveContentResponse
{
    public bool Success { get; init; }
    public string? Error { get; init; }
}
