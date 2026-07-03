using System.ComponentModel;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using MeshWeaver.ContentCollections;
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
    /// <summary>The plugin's display name, "ContentCollection".</summary>
    public string Name => "ContentCollection";

    /// <summary>
    /// Builds the agent tools exposed by this plugin: upload content, chunk search, and chunk read.
    /// </summary>
    /// <returns>The plugin's <see cref="AITool"/> instances.</returns>
    public IEnumerable<AITool> CreateTools()
    {
        return
        [
            AIFunctionFactory.Create(UploadContent),
            AIFunctionFactory.Create(SearchChunks),
            AIFunctionFactory.Create(GetChunk),
        ];
    }

    /// <summary>
    /// Agent tool: semantic search over indexed content chunks, returning the matching chunks with
    /// their (collectionPath, filePath, chunkIndex) coordinates so the agent can read or step through them.
    /// </summary>
    /// <param name="query">Free-text query matched semantically against indexed chunk text.</param>
    /// <param name="scope">Node path to anchor the search at (this path and each ancestor prefix); defaults to the agent's current context.</param>
    /// <param name="limit">Maximum number of chunk hits to return (1-200, default 20); not deduped by file.</param>
    /// <returns>A JSON string of the form <c>{count, results:[{documentPath, collectionPath, filePath, chunkIndex, rank, snippet}]}</c>.</returns>
    [Description(
        "Semantic search over INDEXED content chunks — the chunk-level companion to the node `Search`. " +
        "Where node search resolves a hit up to its Document node and drops the chunk position, this " +
        "returns the matching chunks themselves WITH their (collectionPath, filePath, chunkIndex) so you " +
        "can read the exact window or step through neighbours with get_chunk. Use this to FIND relevant " +
        "passages and gather context; use Get on the Document for whole-document reads (e.g. table " +
        "extraction). Scope it two ways: anchored (the `scope` node path plus each ANCESTOR-prefix " +
        "collection), or targeted by putting `namespace:<node>/<collection>` (with optional " +
        "`scope:subtree|exact|ancestorsandself`) in the query to search ONE collection — `scope:subtree` " +
        "(the default when a namespace is given) checks only that collection and anything nested under it. " +
        "Returns {count, results:[{documentPath, collectionPath, filePath, chunkIndex, rank, snippet}]}.")]
    public Task<string> SearchChunks(
        [Description("Free-text query (matched semantically against indexed chunk text — 1000-char windows, 150-char overlap). May also carry `namespace:<node>/<collection>` and `scope:subtree|exact|ancestorsandself` to target one collection.")] string query,
        [Description("Node path to anchor the search at — this path AND each ancestor prefix are searched (e.g. 'ACME/Reports'). Defaults to the agent's current context; ignored when the query carries a `namespace:` token.")] string? scope = null,
        [Description("Maximum number of chunk hits to return (1-200, default 20). Not deduped by file — chunk-level hits are the point.")] int limit = 20)
    {
        var scopePath = !string.IsNullOrWhiteSpace(scope)
            ? MeshOperations.ResolvePath(MeshOperations.ResolveContextPath(chat, scope))
            : chat.Context?.Context;

        return ChunkNavigation.SearchChunks(hub.ServiceProvider, query, scopePath, limit)
            .FirstAsync().ToTask();
    }

    /// <summary>
    /// Agent tool: reads one indexed content chunk by its 0-based index within a file, with prev/next
    /// links so the agent can step through the file's chunk sequence. Pairs with <see cref="SearchChunks"/>.
    /// </summary>
    /// <param name="collectionPath">The content collection path the chunk belongs to (from a search hit).</param>
    /// <param name="filePath">The file path within the collection (from a search hit).</param>
    /// <param name="chunkIndex">0-based chunk index within the file (or a prev/next index to step).</param>
    /// <returns>A JSON string of the form <c>{found, collectionPath, filePath, chunkIndex, text, prevIndex, nextIndex, totalChunks}</c>.</returns>
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

    /// <summary>
    /// Agent tool: uploads text content (SVG, markdown, JSON, CSS, etc.) into a node's content
    /// collection by posting a <c>SaveContentRequest</c> to the owning node's hub. Times out
    /// after 30 seconds if no response arrives.
    /// </summary>
    /// <param name="nodePath">Canonical path to the node that owns the collection (the node's <c>path</c>, not its display name).</param>
    /// <param name="filePath">File name/path within the collection (e.g. <c>diagram.svg</c>).</param>
    /// <param name="content">The text content to upload.</param>
    /// <param name="collectionName">Collection name; defaults to "content".</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A status message with the uploaded content reference, or the reason the upload failed.</returns>
    [Description("Uploads text content (SVG, markdown, JSON, CSS, etc.) to a node's content collection. Use for storing diagrams, images (SVG), stylesheets, or any text-based files alongside a node.")]
    public Task<string> UploadContent(
        [Description("Canonical path to the node that owns the collection — use the MeshNode's `path` property, NOT its `name`. Use @/full/path for absolute or @relative/path relative to the current context. Example: @/PartnerRe/AIConsulting or @FinalReport. If you only know the display name, call Search('name:\"...\"') first and use the path field of the match.")] string nodePath,
        [Description("File name/path within the collection (e.g., 'diagram.svg', 'images/architecture.svg')")] string filePath,
        [Description("The text content to upload (SVG markup, markdown, JSON, etc.)")] string content,
        [Description("Collection name (default: 'content')")] string collectionName = ContentCollectionsExtensions.DefaultCollectionName,
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
    /// <summary>Name of the target content collection on the node.</summary>
    public required string CollectionName { get; init; }
    /// <summary>File name/path within the collection.</summary>
    public required string FilePath { get; init; }
    /// <summary>The text content to store at <see cref="FilePath"/>.</summary>
    public required string TextContent { get; init; }
}

/// <summary>
/// Response to a <see cref="SaveContentRequest"/> indicating whether the content was saved.
/// </summary>
public record SaveContentResponse
{
    /// <summary>True when the content was saved successfully.</summary>
    public bool Success { get; init; }
    /// <summary>Failure description when <see cref="Success"/> is false; otherwise null.</summary>
    public string? Error { get; init; }
}
