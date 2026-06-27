using System.Reactive.Linq;
using System.Text.Json.Nodes;
using MeshWeaver.ContentCollections.Indexing;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.AI.Plugins;

/// <summary>
/// Tool-facing adapter for the chunk-navigation tools — <c>search_chunks</c> and <c>get_chunk</c> —
/// exposed identically on the agent surface (<see cref="ContentCollectionPlugin"/>) and the MCP surface
/// (<c>McpMeshPlugin</c>). It resolves the store + embedder from the supplied
/// <see cref="IServiceProvider"/> and delegates the actual search to
/// <see cref="ContentChunkSearch"/> in the indexing core — the SAME engine the in-portal "Explore index"
/// GUI calls, so the two transports and the GUI stay in sync by construction.
///
/// <para>This is the chunk-LEVEL retrieval counterpart to node-level <c>search</c>: node search resolves
/// a chunk hit up to its <c>Document</c> node and drops the chunk index (good for "which file"), while
/// <see cref="SearchChunks"/> keeps the <c>(collectionPath, filePath, chunkIndex)</c> coordinate so the
/// caller can read the exact window and <see cref="GetChunk"/> can step prev/next through the file. See
/// the ContentChunkNavigation architecture doc.</para>
/// </summary>
public static class ChunkNavigation
{
    /// <summary>
    /// Embeds <paramref name="query"/> and runs a cosine chunk search, returning a JSON envelope
    /// <c>{count, results:[{ documentPath, collectionPath, filePath, chunkIndex, rank, snippet }]}</c>.
    /// Hits are NOT deduped by file — chunk-level granularity is the point — and are capped at
    /// <paramref name="limit"/>.
    ///
    /// <para>Two modes by query shape (see <see cref="ContentChunkSearch.Search"/>): a query that carries a
    /// <c>namespace:</c> token is resolved from the <c>namespace:/scope:</c> grammar (subtree by default,
    /// "check only this collection"); otherwise the search is anchored at <paramref name="scopePath"/> and
    /// walks that path plus each ancestor prefix.</para>
    /// </summary>
    /// <param name="services">Service provider to resolve the store + embedder from.</param>
    /// <param name="query">
    /// Free-text query, optionally carrying the GitHub-style <c>namespace:&lt;node&gt;/&lt;collection&gt;</c>
    /// and <c>scope:</c> qualifiers. When <c>namespace:</c> is present the grammar wins and
    /// <paramref name="scopePath"/> is ignored.
    /// </param>
    /// <param name="scopePath">
    /// The path the (non-grammar) search is anchored at — this path plus each ancestor prefix. When
    /// null/empty and the query has no <c>namespace:</c>, a result with a hint is returned.
    /// </param>
    /// <param name="limit">Max chunk hits returned (1..200).</param>
    public static IObservable<string> SearchChunks(
        IServiceProvider services, string query, string? scopePath, int limit = 20) =>
        ContentChunkSearch.Search(
                services.GetService<IChunkedContentVectorStore>(),
                services.GetService<IChunkEmbedder>(),
                query, scopePath, limit)
            .Select(ContentChunkSearch.ToJson);

    /// <summary>
    /// Grammar-driven content search returning the TYPED <see cref="ContentSearchResult"/> (resolved
    /// namespace/scope, ranked hits, and the equivalent tool-call descriptor). The thin counterpart to
    /// <see cref="SearchChunks"/> for callers that want structured hits rather than the JSON envelope.
    /// </summary>
    public static IObservable<ContentSearchResult> SearchContent(
        IServiceProvider services, string query, int limit = 20, string? defaultNamespace = null) =>
        ContentChunkSearch.SearchContent(
            services.GetService<IChunkedContentVectorStore>(),
            services.GetService<IChunkEmbedder>(),
            query, limit, defaultNamespace);

    /// <summary>
    /// Reads the single chunk at <paramref name="chunkIndex"/> of <paramref name="filePath"/> within
    /// <paramref name="collectionPath"/>, returning <c>{ collectionPath, filePath, chunkIndex, text,
    /// prevIndex, nextIndex, totalChunks }</c>. <c>prevIndex</c> is null at index 0; <c>nextIndex</c> is
    /// null at the last chunk. A null chunk (out of range, or the file is not indexed) returns a clear
    /// "not found" envelope carrying <c>totalChunks</c> so the caller can see the valid range.
    /// </summary>
    public static IObservable<string> GetChunk(
        IServiceProvider services, string collectionPath, string filePath, int chunkIndex)
    {
        var store = services.GetService<IChunkedContentVectorStore>();
        if (store is null)
            return Observable.Return(NotAvailableEnvelope());

        if (string.IsNullOrWhiteSpace(collectionPath))
            return Observable.Return(Hint("'collectionPath' is required."));
        if (string.IsNullOrWhiteSpace(filePath))
            return Observable.Return(Hint("'filePath' is required."));

        return store.GetChunk(collectionPath, filePath, chunkIndex)
            .SelectMany(chunk => store.GetChunkCount(collectionPath, filePath)
                .Select(total =>
                {
                    if (chunk is null)
                        return new JsonObject
                        {
                            ["found"] = false,
                            ["collectionPath"] = collectionPath,
                            ["filePath"] = filePath,
                            ["chunkIndex"] = chunkIndex,
                            ["totalChunks"] = total,
                            ["message"] = total == 0
                                ? $"No chunks indexed for '{filePath}' in '{collectionPath}'."
                                : $"No chunk at index {chunkIndex}; valid range is 0..{total - 1}.",
                        }.ToJsonString();

                    return new JsonObject
                    {
                        ["found"] = true,
                        ["collectionPath"] = chunk.CollectionPath,
                        ["filePath"] = chunk.FilePath,
                        ["chunkIndex"] = chunk.ChunkIndex,
                        ["text"] = chunk.Text,
                        ["prevIndex"] = chunk.ChunkIndex > 0 ? chunk.ChunkIndex - 1 : null,
                        ["nextIndex"] = chunk.ChunkIndex < total - 1 ? chunk.ChunkIndex + 1 : null,
                        ["totalChunks"] = total,
                    }.ToJsonString();
                }));
    }

    private static string NotAvailableEnvelope() =>
        new JsonObject
        {
            ["count"] = 0,
            ["results"] = new JsonArray(),
            ["message"] = "Content chunk indexing is not enabled in this host — no chunk store is configured.",
        }.ToJsonString();

    private static string Hint(string message) =>
        new JsonObject
        {
            ["count"] = 0,
            ["results"] = new JsonArray(),
            ["message"] = message,
        }.ToJsonString();
}
