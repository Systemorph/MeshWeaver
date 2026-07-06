using System.Reactive;
using System.Reactive.Linq;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeshWeaver.ContentCollections.Indexing;

/// <summary>Outcome of a single <see cref="ContentIndexingService.IndexFile"/> call.</summary>
public enum IndexStatus
{
    /// <summary>The file changed (or was new); its chunks were (re)built, embedded and stored.</summary>
    Indexed,

    /// <summary>The file's content hash matched the stored hash; nothing was re-extracted or re-embedded.</summary>
    Skipped,

    /// <summary>The file produced no extractable text (unknown/binary or empty); no chunks stored.</summary>
    NoText,
}

/// <summary>Result of indexing one file.</summary>
/// <param name="Status">What happened.</param>
/// <param name="ChunkCount">Chunks stored (0 for <see cref="IndexStatus.Skipped"/> / <see cref="IndexStatus.NoText"/>).</param>
public sealed record IndexResult(IndexStatus Status, int ChunkCount)
{
    /// <summary>Creates a result for a file that was (re)indexed, recording how many chunks were stored.</summary>
    /// <param name="chunkCount">The number of chunks stored for the file.</param>
    /// <returns>An <c>IndexResult</c> with <see cref="IndexStatus.Indexed"/> status and the given chunk count.</returns>
    public static IndexResult Indexed(int chunkCount) => new(IndexStatus.Indexed, chunkCount);

    /// <summary>The shared result for a file skipped by the hash gate (unchanged; zero chunks).</summary>
    public static readonly IndexResult Skipped = new(IndexStatus.Skipped, 0);

    /// <summary>The shared result for a file that yielded no extractable text (zero chunks).</summary>
    public static readonly IndexResult NoText = new(IndexStatus.NoText, 0);
}

/// <summary>
/// The orchestrator: content-file bytes → text → chunks → embeddings → vector store. Fully
/// reactive (no <c>async</c>/<c>await</c>/<c>Task</c>), storage-agnostic (composes
/// <see cref="ITextExtractor"/>, <see cref="IChunkEmbedder"/>, <see cref="IChunkedContentVectorStore"/>),
/// and pool-correct: it holds NO I/O-pool slot itself — every leaf (extract, embed, store)
/// acquires and releases its own. The orchestration is pure composition.
/// </summary>
public sealed class ContentIndexingService
{
    private readonly ITextExtractor _extractor;
    private readonly IChunkEmbedder _embedder;
    private readonly IChunkedContentVectorStore _store;
    private readonly ISummarizer? _summarizer;
    private readonly IDocumentSink? _documentSink;
    private readonly ContentIndexingOptions _options;
    private readonly ILogger<ContentIndexingService> _logger;

    /// <summary>
    /// Initializes a new instance of the <c>ContentIndexingService</c> class.
    /// </summary>
    /// <param name="extractor">Extracts plain text from raw file bytes (dispatched by extension).</param>
    /// <param name="embedder">Produces the embedding vector for each chunk of text.</param>
    /// <param name="store">Persists file hashes and chunk sets and serves vector search.</param>
    /// <param name="options">Chunking and concurrency options; defaults are used when <c>null</c>.</param>
    /// <param name="logger">Logger for the indexing pipeline; a no-op logger is used when <c>null</c>.</param>
    /// <param name="summarizer">Optional per-document summarizer; the document branch runs only when both this and <paramref name="documentSink"/> are supplied.</param>
    /// <param name="documentSink">Optional sink that persists the per-file <see cref="DocumentInfo"/>; paired with <paramref name="summarizer"/>.</param>
    public ContentIndexingService(
        ITextExtractor extractor,
        IChunkEmbedder embedder,
        IChunkedContentVectorStore store,
        ContentIndexingOptions? options = null,
        ILogger<ContentIndexingService>? logger = null,
        ISummarizer? summarizer = null,
        IDocumentSink? documentSink = null)
    {
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        // summarizer + documentSink are OPTIONAL: both null ⇒ exactly the chunk-embed-store
        // behavior (no per-file Document, no summary). They are wired together — a document is
        // written only when BOTH are present.
        _summarizer = summarizer;
        _documentSink = documentSink;
        _options = options ?? new ContentIndexingOptions();
        _logger = logger ?? NullLogger<ContentIndexingService>.Instance;
    }

    /// <summary>
    /// Indexes one file. Computes the file's SHA-256, checks the hash gate (skips unchanged files
    /// without re-extracting or re-embedding), then — on change — extracts text, chunks it, embeds
    /// every chunk (bounded merge), and replaces the file's chunks in the store. Cold: subscribe to
    /// run. Emits exactly one <see cref="IndexResult"/>.
    /// </summary>
    /// <param name="force">
    /// When true the hash gate is bypassed: the file is always re-extracted, re-chunked, re-embedded and
    /// its chunks replaced, even if its content is unchanged. This is how a reindex <b>backfills</b> new
    /// per-chunk provenance (page/position) onto files that were indexed before it existed — the content
    /// is identical, so the plain hash gate would skip them forever.
    /// </param>
    public IObservable<IndexResult> IndexFile(
        string collectionPath, string filePath, string fileName, byte[] bytes, bool force = false)
    {
        var hash = ComputeSha256(bytes);

        if (force)
            return IndexChanged(collectionPath, filePath, fileName, bytes, hash);

        return _store.GetFileHash(collectionPath, filePath)
            .Take(1)
            .SelectMany(storedHash =>
            {
                if (string.Equals(storedHash, hash, StringComparison.Ordinal))
                {
                    _logger.LogDebug(
                        "Hash gate: '{FilePath}' in '{Collection}' unchanged ({Hash}); skipping.",
                        filePath, collectionPath, hash);
                    // Chunks are up to date — but the per-file Document may still be MISSING
                    // (chunks indexed before the document branch was wired, or the Document write
                    // failed). A search hit for such a file resolves to a Document node that does
                    // not exist → the click lands on Not Found, and the gate would skip it forever.
                    // Heal: when the document branch is wired and no Document exists, run
                    // extract + summarize + sink for THIS file only (the chunk store is untouched).
                    return EnsureDocument(collectionPath, filePath, fileName, bytes, hash)
                        .Select(_ => IndexResult.Skipped);
                }

                return IndexChanged(collectionPath, filePath, fileName, bytes, hash);
            });
    }

    /// <summary>
    /// The hash-gate HEAL: when the document branch is wired but no <c>Document</c> exists for the
    /// (unchanged) file, extracts the text again and runs the summarize + sink branch, reporting
    /// the stored chunk count. No-op (single <see cref="Unit"/>) when the branch isn't wired, the
    /// Document already exists, or the file has no extractable text.
    /// </summary>
    private IObservable<Unit> EnsureDocument(
        string collectionPath, string filePath, string fileName, byte[] bytes, string hash)
    {
        if (_summarizer is null || _documentSink is null)
            return Observable.Return(Unit.Default);

        return _documentSink.DocumentExists(collectionPath, filePath)
            .Take(1)
            .SelectMany(exists =>
            {
                if (exists)
                    return Observable.Return(Unit.Default);

                _logger.LogInformation(
                    "Hash gate: '{FilePath}' in '{Collection}' unchanged but its Document is missing; healing.",
                    filePath, collectionPath);
                return _store.GetChunkCount(collectionPath, filePath)
                    .Take(1)
                    .SelectMany(chunkCount => _extractor.ExtractText(fileName, bytes)
                        .Take(1)
                        .SelectMany(text => string.IsNullOrWhiteSpace(text)
                            ? Observable.Return(Unit.Default)
                            : WriteDocumentBranch(
                                collectionPath, filePath, fileName, text, bytes, hash, chunkCount)));
            });
    }

    private IObservable<IndexResult> IndexChanged(
        string collectionPath, string filePath, string fileName, byte[] bytes, string hash) =>
        _extractor.ExtractDocument(fileName, bytes)
            .Take(1)
            .SelectMany(document =>
            {
                var chunks = TextChunker.ChunkPositioned(document, _options.ChunkSize, _options.ChunkOverlap);
                if (chunks.Count == 0)
                {
                    _logger.LogInformation(
                        "No text extracted from '{FilePath}' in '{Collection}'; recording NoText.",
                        filePath, collectionPath);
                    // Replace with an empty set so any stale chunks for this file are removed and
                    // the (now absent) hash forces a re-attempt if the file later gains content.
                    return _store.ReplaceFileChunks(collectionPath, filePath, [])
                        .Select(_ => IndexResult.NoText);
                }

                // Chunk branch: embed every chunk (carrying its page/position provenance), replace the
                // file's stored chunk set, and report how many were stored.
                var chunkBranch = EmbedChunks(collectionPath, filePath, hash, chunks)
                    .SelectMany(embedded =>
                        _store.ReplaceFileChunks(collectionPath, filePath, embedded)
                            .Select(_ => embedded.Count));

                // Document branch: ONE summarize per document (never per chunk), then write the
                // per-file Document via the sink. Only runs when BOTH summarizer + sink are wired;
                // otherwise it's a no-op that emits a single Unit so the Zip below still completes
                // — giving exactly today's behavior when either is absent. The summarize call is
                // its own IIoPool leaf inside the ISummarizer impl; the orchestration holds no slot.
                return chunkBranch.SelectMany(storedCount =>
                    WriteDocumentBranch(collectionPath, filePath, fileName, document.Text, bytes, hash, storedCount)
                        .Select(_ => IndexResult.Indexed(storedCount)));
            });

    /// <summary>
    /// Summarizes the document (once) and writes the per-file <see cref="DocumentInfo"/> through the
    /// sink — but ONLY when both <see cref="ISummarizer"/> and <see cref="IDocumentSink"/> are wired.
    /// When either is absent this is a no-op emitting a single <see cref="Unit"/>, so the caller's
    /// composition completes identically to the original (chunk-only) flow.
    /// </summary>
    private IObservable<Unit> WriteDocumentBranch(
        string collectionPath, string filePath, string fileName, string text, byte[] bytes,
        string hash, int chunkCount)
    {
        if (_summarizer is null || _documentSink is null)
            return Observable.Return(Unit.Default);

        var summarizer = _summarizer;
        var documentSink = _documentSink;

        return summarizer.Summarize(text, fileName)
            .Take(1)
            .SelectMany(summary => documentSink.WriteDocument(new DocumentInfo(
                CollectionPath: collectionPath,
                FilePath: filePath,
                FileName: fileName,
                Summary: summary,
                ContentHash: hash,
                Mime: null,
                SizeBytes: bytes.LongLength,
                ChunkCount: chunkCount)));
    }

    /// <summary>
    /// Embeds every chunk's text via the embedder, bounded-merged (NOT a held-slot serial loop), and
    /// reassembles them back into <see cref="ContentChunk"/>s in their original order — each carrying its
    /// page/position provenance from the <see cref="PositionedChunk"/>.
    /// </summary>
    private IObservable<IReadOnlyList<ContentChunk>> EmbedChunks(
        string collectionPath, string filePath, string hash, IReadOnlyList<PositionedChunk> chunks) =>
        chunks
            // Pair each chunk with its index up front so order survives the unordered merge.
            .Select((chunk, index) =>
                _embedder.Embed(chunk.Text)
                    .Take(1)
                    .Select(embedding => new ContentChunk(
                        CollectionPath: collectionPath,
                        FilePath: filePath,
                        ChunkIndex: index,
                        Text: chunk.Text,
                        ContentHash: hash,
                        Embedding: embedding,
                        Page: chunk.Page,
                        Position: chunk.Position)))
            .ToObservable()
            // Each Embed leaf acquires its OWN pool slot; this merge just bounds how many we
            // subscribe to at once. The orchestration holds no slot while they run.
            .Merge(Math.Max(1, _options.MaxConcurrentEmbeddings))
            .ToList()
            .Select(chunks => (IReadOnlyList<ContentChunk>)chunks
                .OrderBy(c => c.ChunkIndex)
                .ToArray());

    private static string ComputeSha256(byte[] bytes)
    {
        var digest = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(digest);
    }
}
