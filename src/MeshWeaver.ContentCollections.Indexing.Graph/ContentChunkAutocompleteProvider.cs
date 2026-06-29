using System.Reactive.Linq;
using MeshWeaver.Data.Completion;

namespace MeshWeaver.ContentCollections.Indexing.Graph;

/// <summary>
/// Autocomplete + content-search over INDEXED content chunks (the vector store), resolving every hit
/// back to the indexed file's <c>Document</c> mesh node.
///
/// <para>On a text query this provider embeds the query through the injected
/// <see cref="IChunkEmbedder"/> (one leaf), runs a cosine <see cref="IChunkedContentVectorStore.Search"/>
/// over the collection(s) in scope, and projects each chunk hit to an <see cref="AutocompleteItem"/>
/// that references the source file's <c>Document</c> node at
/// <see cref="DocumentPaths.For(string,string)"/> — so selecting a completion inserts an <c>@</c>-ref
/// to the Document (not to the raw chunk). Hits are <b>deduped by source file</b> (one completion per
/// file, keeping the best-scoring chunk), because a single file produces many chunks and the user
/// wants the FILE, surfaced with a snippet of the matching chunk.</para>
///
/// <para>This complements node-level search: <see cref="MeshDocumentSink"/> already writes each indexed
/// file as a first-class, name+summary-searchable <c>Document</c> node. This provider adds the
/// chunk-level <i>semantic</i> hit (a query that matches a file's CONTENT rather than its name) and
/// resolves it to that same Document node.</para>
///
/// <para>Fully reactive — the embed and the search are composed leaves
/// (<c>SelectMany</c>), never awaited. The collection scope is supplied by a resolver delegate so the
/// provider stays unit-testable without a live mesh: DI wires a default that derives the scope from the
/// caller's <c>contextPath</c> / namespace; a test passes an explicit scope.</para>
/// </summary>
public sealed class ContentChunkAutocompleteProvider : IAutocompleteProvider
{
    /// <summary>The UCR prefix this provider answers — chunk/document content search.</summary>
    public const string PrefixValue = "document";

    private readonly IChunkedContentVectorStore _store;
    private readonly IChunkEmbedder _embedder;
    private readonly Func<string?, IReadOnlyCollection<string>> _collectionScope;
    private readonly int _topK;

    /// <param name="store">The chunked content vector store to search.</param>
    /// <param name="embedder">Embeds the query text into the store's vector space.</param>
    /// <param name="collectionScope">
    /// Maps the autocomplete <c>contextPath</c> (the namespace the @-reference is being typed in) to the
    /// set of collection paths to search. DI supplies a default that derives this from the context; a
    /// unit test supplies a fixed scope.
    /// </param>
    /// <param name="topK">Max chunk hits to pull from the store per collection (pre-dedup).</param>
    public ContentChunkAutocompleteProvider(
        IChunkedContentVectorStore store,
        IChunkEmbedder embedder,
        Func<string?, IReadOnlyCollection<string>> collectionScope,
        int topK = 20)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _collectionScope = collectionScope ?? throw new ArgumentNullException(nameof(collectionScope));
        _topK = topK;
    }

    /// <inheritdoc />
    public string? Prefix => PrefixValue;

    /// <inheritdoc />
    public IObservable<IReadOnlyCollection<AutocompleteItem>> GetItems(string query, string? contextPath = null)
    {
        var searchText = ExtractSearchText(query);

        // A semantic search has no meaning for an empty query — emit the empty snapshot rather than
        // embedding "" (and NEVER Observable.Empty, which would stall the aggregator's CombineLatest).
        if (string.IsNullOrWhiteSpace(searchText))
            return Observable.Return(AutocompleteSnapshots.Empty);

        var collections = _collectionScope(contextPath);
        if (collections.Count == 0)
            return Observable.Return(AutocompleteSnapshots.Empty);

        // Embed once (a single leaf), then fan the SAME query vector across every in-scope collection's
        // cosine search. Each chunk hit becomes a candidate item; AutocompleteSnapshots.FromItems dedups
        // by InsertText (the Document node path) so a file that produced several matching chunks collapses
        // to ONE completion — and because items arrive best-first per collection and Scan keeps the
        // highest-priority entry per InsertText, the surviving completion carries the best-scoring chunk.
        var items = _embedder.Embed(searchText)
            .SelectMany(vector => collections.ToObservable()
                .SelectMany(collection => _store.Search(collection, vector, _topK)
                    .SelectMany(hits => hits.ToObservable()))
                .Select((hit, rank) => ToAutocompleteItem(hit, rank)));

        return AutocompleteSnapshots.FromItems(items, 50);
    }

    /// <summary>
    /// Projects one chunk hit to an item that references the file's <c>Document</c> node. Label = the
    /// file name (last path segment), Description = a trimmed snippet of the matching chunk text,
    /// InsertText/Path = the deterministic Document node path (so selecting inserts an @-ref to the
    /// Document). <paramref name="rank"/> is the best-first order the store returned hits in, used to
    /// turn cosine order into a descending Priority so the first (most similar) chunk wins the per-file
    /// dedup.
    /// </summary>
    private static AutocompleteItem ToAutocompleteItem(ContentChunk hit, int rank)
    {
        var documentPath = DocumentPaths.For(hit.CollectionPath, hit.FilePath);
        var fileName = LastSegment(hit.FilePath);
        var snippet = Snippet(hit.Text);

        // The store returns hits most-similar-first; map that order to a descending priority so the
        // best chunk for a given file survives the InsertText dedup in FromItems.
        var priority = 10_000 - rank;

        return new AutocompleteItem(
            Label: fileName,
            InsertText: $"@{documentPath} ",
            Description: snippet,
            Category: "Indexed content",
            Priority: priority,
            Kind: AutocompleteKind.File,
            Icon: null,
            Path: documentPath);
    }

    /// <summary>Last path segment of a (possibly nested) file path, ignoring trailing separators.</summary>
    private static string LastSegment(string filePath)
    {
        var trimmed = filePath.Replace('\\', '/').TrimEnd('/');
        var lastSlash = trimmed.LastIndexOf('/');
        var name = lastSlash >= 0 ? trimmed[(lastSlash + 1)..] : trimmed;
        return string.IsNullOrEmpty(name) ? filePath : name;
    }

    /// <summary>A single-line, length-bounded snippet of the chunk text for the dropdown description.</summary>
    private static string Snippet(string text)
    {
        const int max = 160;
        var oneLine = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        // Collapse runs of whitespace introduced by the newline replacement.
        oneLine = string.Join(' ', oneLine.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return oneLine.Length <= max ? oneLine : oneLine[..max].TrimEnd() + "…";
    }

    /// <summary>
    /// Extracts the free-text search portion of the raw query: strips a leading <c>@</c> and an optional
    /// <c>document:</c> / <c>document/</c> UCR tag, mirroring <c>ContentAutocompleteProvider</c>'s tag
    /// handling so the prefixed and bare query forms both reach the embedder as plain text.
    /// </summary>
    private static string ExtractSearchText(string query)
    {
        if (string.IsNullOrEmpty(query))
            return string.Empty;

        var text = query.TrimStart('@');

        foreach (var tag in new[] { PrefixValue + ":", PrefixValue + "/" })
        {
            var idx = text.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                text = text[(idx + tag.Length)..];
                break;
            }
        }

        return text.Trim();
    }
}
