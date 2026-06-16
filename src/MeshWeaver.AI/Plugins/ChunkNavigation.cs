using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.ContentCollections.Indexing;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.AI.Plugins;

/// <summary>
/// Shared backing logic for the chunk-navigation tools — <c>search_chunks</c> and <c>get_chunk</c> —
/// exposed identically on the agent surface (<see cref="ContentCollectionPlugin"/>) and the MCP surface
/// (<c>McpMeshPlugin</c>). Both call these methods so the two transports stay in sync by construction.
///
/// <para>This is the chunk-LEVEL retrieval counterpart to node-level <c>search</c>: node search resolves
/// a chunk hit up to its <c>Document</c> node and drops the chunk index (good for "which file"), while
/// <see cref="SearchChunks"/> keeps the <c>(collectionPath, filePath, chunkIndex)</c> coordinate so the
/// caller can read the exact window and <see cref="GetChunk"/> can step prev/next through the file. See
/// the ContentChunkNavigation architecture doc.</para>
///
/// <para>Reactive throughout: every method returns a cold <see cref="IObservable{T}"/> that performs the
/// embed + store read on subscribe (the tool boundary bridges with <c>.FirstAsync().ToTask()</c>). The
/// store (<see cref="IChunkedContentVectorStore"/>) and embedder (<see cref="IChunkEmbedder"/>) are
/// resolved from the supplied <see cref="IServiceProvider"/>; when content indexing is not wired into the
/// host they are absent and the methods emit a clear "not available" envelope instead of throwing.</para>
/// </summary>
public static class ChunkNavigation
{
    /// <summary>
    /// Embeds <paramref name="query"/> and runs a cosine chunk search across the in-scope collection(s),
    /// returning a JSON envelope <c>{count, results:[{ documentPath, collectionPath, filePath, chunkIndex,
    /// score, snippet }]}</c>. Hits are NOT deduped by file — chunk-level granularity is the point — and
    /// are capped at <paramref name="limit"/>.
    /// </summary>
    /// <param name="services">Service provider to resolve the store + embedder from.</param>
    /// <param name="query">Free-text query embedded into the store's vector space.</param>
    /// <param name="scopePath">
    /// The path the search is anchored at. The collection candidates are this path plus each ancestor
    /// prefix (the same ancestor-walk the autocomplete provider uses). When null/empty an empty result
    /// with a hint to pass a scope is returned — a bare, context-free query has no collection to search.
    /// </param>
    /// <param name="limit">Max chunk hits returned (1..200).</param>
    public static IObservable<string> SearchChunks(
        IServiceProvider services, string query, string? scopePath, int limit = 20)
    {
        var store = services.GetService<IChunkedContentVectorStore>();
        var embedder = services.GetService<IChunkEmbedder>();
        if (store is null || embedder is null)
            return Observable.Return(NotAvailableEnvelope());

        if (string.IsNullOrWhiteSpace(query))
            return Observable.Return(Hint("Pass a non-empty 'query' to search content chunks."));

        var collections = CollectionScope(scopePath);
        if (collections.Count == 0)
            return Observable.Return(Hint(
                "No scope to search. Pass 'scope' as the node path whose content (and ancestors') chunks to search " +
                "— e.g. 'ACME/Reports' — or run this from an agent with a context path."));

        limit = Math.Clamp(limit, 1, 200);

        // Embed once, fan the SAME vector across every in-scope collection's cosine search, project each
        // chunk hit (best-first per collection), and cap at the limit. No dedup — chunk-level hits are
        // the point. The embed + searches are composed leaves (SelectMany), never awaited.
        return embedder.Embed(query)
            .SelectMany(vector => collections.ToObservable()
                .SelectMany(collection => store.Search(collection, vector, limit)
                    .SelectMany(hits => hits.Select((hit, rank) => (hit, rank)).ToObservable()))
                .ToList())
            .Select(hits =>
            {
                var results = new JsonArray();
                // The store returns hits most-similar-first but does NOT surface the raw cosine score on
                // ContentChunk, so we report 'rank' (0-based best-first position) as the relevance signal
                // rather than fabricate a score. Re-rank globally because hits arrived per-collection.
                var rank = 0;
                foreach (var (hit, _) in hits.Take(limit))
                {
                    results.Add(new JsonObject
                    {
                        ["documentPath"] = DocumentPaths.For(hit.CollectionPath, hit.FilePath),
                        ["collectionPath"] = hit.CollectionPath,
                        ["filePath"] = hit.FilePath,
                        ["chunkIndex"] = hit.ChunkIndex,
                        ["rank"] = rank++,
                        ["snippet"] = Snippet(hit.Text),
                    });
                }

                return new JsonObject
                {
                    ["count"] = results.Count,
                    ["results"] = results,
                }.ToJsonString();
            });
    }

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

    /// <summary>
    /// The collection candidates for a scope path: the path itself plus every ancestor prefix, so a scope
    /// of <c>part/Space/Sub</c> searches collections keyed at <c>part/Space/Sub</c>, <c>part/Space</c>, and
    /// <c>part</c>. Replicates <c>DocumentIndexingExtensions.CollectionScopeFromContext</c> locally so this
    /// helper need not depend on <c>MeshWeaver.ContentCollections.Indexing.Graph</c>. Empty when there is
    /// no scope.
    /// </summary>
    private static IReadOnlyCollection<string> CollectionScope(string? scopePath)
    {
        if (string.IsNullOrWhiteSpace(scopePath))
            return [];

        var scope = new List<string>();
        var path = scopePath.Trim().Trim('/');
        // Strip a leading '@' (agent/MCP paths may arrive @-prefixed) before walking ancestors.
        path = path.TrimStart('@').Trim('/');
        while (path.Length > 0)
        {
            scope.Add(path);
            var lastSlash = path.LastIndexOf('/');
            if (lastSlash <= 0)
                break;
            path = path[..lastSlash];
        }
        return scope;
    }

    /// <summary>A single-line, ~160-char snippet of a chunk's text for the search result.</summary>
    private static string Snippet(string text)
    {
        const int max = 160;
        var oneLine = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        oneLine = string.Join(' ', oneLine.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return oneLine.Length <= max ? oneLine : oneLine[..max].TrimEnd() + "…";
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
