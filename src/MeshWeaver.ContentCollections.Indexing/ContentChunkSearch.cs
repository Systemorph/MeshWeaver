using System.Reactive.Linq;
using System.Text.Json.Nodes;
using MeshWeaver.Mesh;

namespace MeshWeaver.ContentCollections.Indexing;

/// <summary>
/// The storage-agnostic content-chunk search engine shared by every surface: the agent / MCP
/// <c>search_chunks</c> tool (via <c>ChunkNavigation</c> in <c>MeshWeaver.AI</c>) and the in-portal
/// "Explore index" GUI (via the Content-Indexing settings tab). Keeping it here — in the indexing core
/// both of those reference — means the GUI's "this maps to a tool call" claim is honest by construction:
/// the same code resolves the query for the agent and for the explorer.
///
/// <para><b>Two scope models.</b>
/// <list type="bullet">
///   <item><b>Anchored</b> (<see cref="SearchAnchored"/>): the caller passes a node path; the collection
///     candidates are that path plus each ancestor prefix ("what content is relevant to where I am").
///     This is the agent's context-anchored retrieval.</item>
///   <item><b>Targeted grammar</b> (<see cref="SearchContent"/>): the query carries
///     <c>namespace:&lt;node&gt;/&lt;collection&gt;</c> and an optional <c>scope:</c> qualifier. The
///     default scope is <c>subtree</c> — the named collection plus every collection nested under it
///     ("check only this collection"). <c>scope:exact</c> restricts to the one collection;
///     <c>scope:ancestors</c>/<c>ancestorsandself</c> reproduce the ancestor walk.</item>
/// </list>
/// <see cref="Search"/> dispatches by query shape: a <c>namespace:</c> token routes to the grammar engine,
/// otherwise the anchored walk runs.</para>
///
/// <para>Reactive + cold: every method returns an <see cref="IObservable{T}"/> that embeds + reads the
/// store on subscribe. The store + embedder are passed in (resolved by the caller from DI); when content
/// indexing is not wired into a host they are null and the engine emits a result whose
/// <see cref="ContentSearchResult.Message"/> explains why, never throwing.</para>
/// </summary>
public static class ContentChunkSearch
{
    /// <summary>
    /// Dispatches by query shape: a query carrying a <c>namespace:</c> token routes to the targeted
    /// grammar engine (<see cref="SearchContent"/>, subtree by default); otherwise the query is anchored at
    /// <paramref name="anchorPath"/> and walks that path plus each ancestor prefix
    /// (<see cref="SearchAnchored"/>).
    /// </summary>
    public static IObservable<ContentSearchResult> Search(
        IChunkedContentVectorStore? store, IChunkEmbedder? embedder,
        string query, string? anchorPath, int limit = 20) =>
        MentionsNamespace(query)
            ? SearchContent(store, embedder, query, limit, defaultNamespace: anchorPath)
            : SearchAnchored(store, embedder, query, anchorPath, limit);

    /// <summary>
    /// Targeted grammar search: parses <paramref name="query"/> for
    /// <c>namespace:&lt;node&gt;/&lt;collection&gt;</c> (which collection) and <c>scope:</c> (how to resolve
    /// collections) plus the free text (embedded), and returns the ranked hits. The default scope is
    /// <c>subtree</c> ("check only this collection") when no <c>scope:</c> is given.
    /// </summary>
    public static IObservable<ContentSearchResult> SearchContent(
        IChunkedContentVectorStore? store, IChunkEmbedder? embedder,
        string query, int limit = 20, string? defaultNamespace = null)
    {
        var parsed = new QueryParser().Parse(query);
        var ns = NormalizePath(parsed.Path ?? defaultNamespace);
        var text = (parsed.TextSearch ?? string.Empty).Trim();
        // The parser forces scope:Children when namespace: is used WITHOUT an explicit scope; detect the
        // explicit form off the raw string so the content-search default (subtree) wins when it's absent.
        var explicitScope = !string.IsNullOrEmpty(query)
            && query.Contains("scope:", StringComparison.OrdinalIgnoreCase);
        var scope = ResolveScope(parsed.Scope, explicitScope);
        limit = Math.Clamp(limit, 1, 200);

        var toolCall = BuildToolCall(ns, scope, text, limit);

        ContentSearchResult Empty(string message) =>
            new(text, ns, scope, limit, Array.Empty<ChunkHit>(), toolCall, message);

        if (store is null || embedder is null)
            return Observable.Return(Empty(
                "Content chunk indexing is not enabled in this host — no chunk store is configured."));
        if (string.IsNullOrWhiteSpace(text))
            return Observable.Return(Empty("Pass query text (free-text terms) to search content chunks."));
        if (string.IsNullOrWhiteSpace(ns))
            return Observable.Return(Empty(
                "No namespace to search. Add 'namespace:<node>/<collection>' to the query " +
                "(e.g. 'namespace:ACME/content laptop pricing')."));

        return embedder.Embed(text)
            .SelectMany(vector => ResolveChunks(store, ns!, scope, vector, limit))
            .Select(chunks => new ContentSearchResult(
                text, ns, scope, limit, ToHits(chunks, limit), toolCall));
    }

    /// <summary>
    /// Anchored search: embeds the query and searches the collection at <paramref name="anchorPath"/> plus
    /// each ancestor-prefix collection, most-similar-first, capped at <paramref name="limit"/>. This is the
    /// agent's context-anchored retrieval (no <c>namespace:</c> token in the query).
    /// </summary>
    private static IObservable<ContentSearchResult> SearchAnchored(
        IChunkedContentVectorStore? store, IChunkEmbedder? embedder,
        string query, string? anchorPath, int limit)
    {
        var text = (query ?? string.Empty).Trim();
        limit = Math.Clamp(limit, 1, 200);
        var toolCall = BuildAnchoredToolCall(anchorPath, text, limit);

        ContentSearchResult Empty(string message) =>
            new(text, NormalizePath(anchorPath), ContentSearchScope.AncestorsAndSelf, limit,
                Array.Empty<ChunkHit>(), toolCall, message);

        if (store is null || embedder is null)
            return Observable.Return(Empty(
                "Content chunk indexing is not enabled in this host — no chunk store is configured."));
        if (string.IsNullOrWhiteSpace(text))
            return Observable.Return(Empty("Pass a non-empty 'query' to search content chunks."));

        var collections = CollectionScope(anchorPath);
        if (collections.Count == 0)
            return Observable.Return(Empty(
                "No scope to search. Pass 'scope' as the node path whose content (and ancestors') chunks to " +
                "search — e.g. 'ACME/Reports' — or use 'namespace:<node>/<collection>' in the query."));

        return embedder.Embed(text)
            .SelectMany(vector => collections.ToObservable()
                .SelectMany(collection => store.Search(collection, vector, limit))
                .SelectMany(list => list.ToObservable())
                .ToList())
            .Select(chunks => new ContentSearchResult(
                text, NormalizePath(anchorPath), ContentSearchScope.AncestorsAndSelf, limit,
                ToHits((IReadOnlyList<ContentChunk>)chunks, limit), toolCall));
    }

    /// <summary>Resolves the chunk set for a namespace + scope into the store call(s) it implies.</summary>
    private static IObservable<IReadOnlyList<ContentChunk>> ResolveChunks(
        IChunkedContentVectorStore store, string ns, ContentSearchScope scope, float[] vector, int limit) =>
        scope switch
        {
            ContentSearchScope.Exact => store.Search(ns, vector, limit),
            ContentSearchScope.AncestorsAndSelf => CollectionScope(ns).ToObservable()
                .SelectMany(collection => store.Search(collection, vector, limit))
                .SelectMany(list => list.ToObservable())
                .ToList()
                .Select(merged => (IReadOnlyList<ContentChunk>)merged),
            _ => store.SearchSubtree(ns, vector, limit), // Subtree (default)
        };

    /// <summary>Serializes a result to the tool's JSON envelope <c>{count, results, message?}</c>.</summary>
    public static string ToJson(ContentSearchResult result)
    {
        var results = new JsonArray();
        foreach (var hit in result.Hits)
        {
            var hitObj = new JsonObject
            {
                ["documentPath"] = hit.DocumentPath,
                ["collectionPath"] = hit.CollectionPath,
                ["filePath"] = hit.FilePath,
                ["chunkIndex"] = hit.ChunkIndex,
                ["rank"] = hit.Rank,
                ["snippet"] = hit.Snippet,
            };
            // Provenance (only when the source carried it): the page the chunk begins on and its normalized
            // box there, so a consumer can cite the page and open+mark the exact region.
            if (hit.Page is int page)
                hitObj["page"] = page;
            if (ChunkPositionJson.ToJsonObject(hit.Position) is { } bbox)
                hitObj["bbox"] = bbox;
            results.Add(hitObj);
        }

        var obj = new JsonObject
        {
            ["count"] = results.Count,
            ["results"] = results,
        };
        if (!string.IsNullOrEmpty(result.Message))
            obj["message"] = result.Message;
        return obj.ToJsonString();
    }

    /// <summary>
    /// The collection candidates for a scope path: the path itself plus every ancestor prefix, so a scope
    /// of <c>part/Space/Sub</c> searches collections keyed at <c>part/Space/Sub</c>, <c>part/Space</c>, and
    /// <c>part</c>. Empty when there is no scope.
    /// </summary>
    private static IReadOnlyCollection<string> CollectionScope(string? scopePath)
    {
        var path = NormalizePath(scopePath);
        if (path is null)
            return [];

        var scope = new List<string>();
        while (path!.Length > 0)
        {
            scope.Add(path);
            var lastSlash = path.LastIndexOf('/');
            if (lastSlash <= 0)
                break;
            path = path[..lastSlash];
        }
        return scope;
    }

    /// <summary>Projects ranked chunks into the capped, 0-based-ranked hit list shared by every surface.</summary>
    private static IReadOnlyList<ChunkHit> ToHits(IEnumerable<ContentChunk> chunks, int limit) =>
        chunks.Take(limit)
            .Select((hit, rank) => new ChunkHit(
                DocumentPaths.For(hit.CollectionPath, hit.FilePath),
                hit.CollectionPath, hit.FilePath, hit.ChunkIndex, rank, Snippet(hit.Text),
                hit.Page, hit.Position))
            .ToArray();

    /// <summary>Maps a parsed <see cref="QueryScope"/> to the content-search scope, defaulting to subtree.</summary>
    private static ContentSearchScope ResolveScope(QueryScope parsed, bool explicitScope)
    {
        if (!explicitScope)
            return ContentSearchScope.Subtree; // content-search default — "check only this collection"
        return parsed switch
        {
            QueryScope.Exact => ContentSearchScope.Exact,
            QueryScope.Ancestors or QueryScope.AncestorsAndSelf => ContentSearchScope.AncestorsAndSelf,
            _ => ContentSearchScope.Subtree, // subtree / descendants / hierarchy / children / nextlevel
        };
    }

    /// <summary>The equivalent grammar <c>search_chunks</c> call for a resolved query — surfaced in the explorer.</summary>
    private static string BuildToolCall(string? ns, ContentSearchScope scope, string text, int limit)
    {
        var scopeToken = scope switch
        {
            ContentSearchScope.Exact => "exact",
            ContentSearchScope.AncestorsAndSelf => "ancestorsandself",
            _ => "subtree",
        };
        var grammar = string.Join(' ', new[]
        {
            string.IsNullOrEmpty(ns) ? null : $"namespace:{ns}",
            $"scope:{scopeToken}",
            string.IsNullOrEmpty(text) ? null : text,
        }.Where(s => s is not null));
        return $"search_chunks(query: \"{grammar}\", limit: {limit})";
    }

    /// <summary>The equivalent anchored <c>search_chunks</c> call (scope is a node path, not the grammar).</summary>
    private static string BuildAnchoredToolCall(string? anchorPath, string text, int limit)
    {
        var ns = NormalizePath(anchorPath);
        var scopeArg = ns is null ? string.Empty : $", scope: \"{ns}\"";
        return $"search_chunks(query: \"{text}\"{scopeArg}, limit: {limit})";
    }

    /// <summary>Whether the query carries a <c>namespace:</c> qualifier (grammar mode).</summary>
    private static bool MentionsNamespace(string? query) =>
        !string.IsNullOrEmpty(query)
        && query.Contains("namespace:", StringComparison.OrdinalIgnoreCase);

    /// <summary>Trims a path of a leading <c>@</c>, surrounding whitespace, and slashes; null when empty.</summary>
    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        var p = path.Trim().TrimStart('@').Trim().Trim('/');
        return p.Length == 0 ? null : p;
    }

    /// <summary>A single-line, ~160-char snippet of a chunk's text for the search result.</summary>
    private static string Snippet(string text)
    {
        const int max = 160;
        var oneLine = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        oneLine = string.Join(' ', oneLine.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return oneLine.Length <= max ? oneLine : oneLine[..max].TrimEnd() + "…";
    }
}

/// <summary>A single chunk hit with its coordinates — the typed form behind the JSON envelope.</summary>
/// <param name="DocumentPath">The hit's <c>Document</c>-node path (<c>DocumentPaths.For</c>), for linking.</param>
/// <param name="CollectionPath">The collection the chunk belongs to.</param>
/// <param name="FilePath">The file within the collection.</param>
/// <param name="ChunkIndex">The 0-based chunk index within the file (feed back into <c>get_chunk</c>).</param>
/// <param name="Rank">0-based best-first relevance position.</param>
/// <param name="Snippet">A one-line, ~160-char preview of the chunk text.</param>
/// <param name="Page">One-based source page the chunk begins on, or null when the source carries no layout.</param>
/// <param name="Position">The chunk's normalized on-page box, or null when unknown — for open+mark.</param>
public record ChunkHit(
    string DocumentPath, string CollectionPath, string FilePath, int ChunkIndex, int Rank, string Snippet,
    int? Page = null, ChunkPosition? Position = null);

/// <summary>
/// The outcome of a content search: the resolved namespace + scope, the ranked hits, and a human-readable
/// descriptor of the equivalent <c>search_chunks</c> call (for the explorer's tool-call inspector).
/// <see cref="Message"/> is set instead of hits when the search could not run (indexing off, no namespace,
/// empty text).
/// </summary>
public record ContentSearchResult(
    string Text,
    string? Namespace,
    ContentSearchScope Scope,
    int Limit,
    IReadOnlyList<ChunkHit> Hits,
    string ToolCall,
    string? Message = null);

/// <summary>How a content search resolves which collections to read from a single named namespace.</summary>
public enum ContentSearchScope
{
    /// <summary>The named collection plus every collection nested under it — the default ("check only this collection").</summary>
    Subtree,

    /// <summary>Only the exact named collection.</summary>
    Exact,

    /// <summary>The named collection plus each ancestor-prefix collection (the agent's context walk).</summary>
    AncestorsAndSelf,
}
