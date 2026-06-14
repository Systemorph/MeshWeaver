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
    public static IndexResult Indexed(int chunkCount) => new(IndexStatus.Indexed, chunkCount);
    public static readonly IndexResult Skipped = new(IndexStatus.Skipped, 0);
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
    private readonly ContentIndexingOptions _options;
    private readonly ILogger<ContentIndexingService> _logger;

    public ContentIndexingService(
        ITextExtractor extractor,
        IChunkEmbedder embedder,
        IChunkedContentVectorStore store,
        ContentIndexingOptions? options = null,
        ILogger<ContentIndexingService>? logger = null)
    {
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? new ContentIndexingOptions();
        _logger = logger ?? NullLogger<ContentIndexingService>.Instance;
    }

    /// <summary>
    /// Indexes one file. Computes the file's SHA-256, checks the hash gate (skips unchanged files
    /// without re-extracting or re-embedding), then — on change — extracts text, chunks it, embeds
    /// every chunk (bounded merge), and replaces the file's chunks in the store. Cold: subscribe to
    /// run. Emits exactly one <see cref="IndexResult"/>.
    /// </summary>
    public IObservable<IndexResult> IndexFile(
        string collectionPath, string filePath, string fileName, byte[] bytes)
    {
        var hash = ComputeSha256(bytes);

        return _store.GetFileHash(collectionPath, filePath)
            .Take(1)
            .SelectMany(storedHash =>
            {
                if (string.Equals(storedHash, hash, StringComparison.Ordinal))
                {
                    _logger.LogDebug(
                        "Hash gate: '{FilePath}' in '{Collection}' unchanged ({Hash}); skipping.",
                        filePath, collectionPath, hash);
                    return Observable.Return(IndexResult.Skipped);
                }

                return IndexChanged(collectionPath, filePath, fileName, bytes, hash);
            });
    }

    private IObservable<IndexResult> IndexChanged(
        string collectionPath, string filePath, string fileName, byte[] bytes, string hash) =>
        _extractor.ExtractText(fileName, bytes)
            .Take(1)
            .SelectMany(text =>
            {
                var chunkTexts = TextChunker.Chunk(text, _options.ChunkSize, _options.ChunkOverlap);
                if (chunkTexts.Count == 0)
                {
                    _logger.LogInformation(
                        "No text extracted from '{FilePath}' in '{Collection}'; recording NoText.",
                        filePath, collectionPath);
                    // Replace with an empty set so any stale chunks for this file are removed and
                    // the (now absent) hash forces a re-attempt if the file later gains content.
                    return _store.ReplaceFileChunks(collectionPath, filePath, [])
                        .Select(_ => IndexResult.NoText);
                }

                return EmbedChunks(collectionPath, filePath, hash, chunkTexts)
                    .SelectMany(chunks =>
                        _store.ReplaceFileChunks(collectionPath, filePath, chunks)
                            .Select(_ => IndexResult.Indexed(chunks.Count)));
            });

    /// <summary>
    /// Embeds every chunk text via the embedder, bounded-merged (NOT a held-slot serial loop), and
    /// reassembles them back into <see cref="ContentChunk"/>s in their original order.
    /// </summary>
    private IObservable<IReadOnlyList<ContentChunk>> EmbedChunks(
        string collectionPath, string filePath, string hash, IReadOnlyList<string> chunkTexts) =>
        chunkTexts
            // Pair each text with its index up front so order survives the unordered merge.
            .Select((chunkText, index) =>
                _embedder.Embed(chunkText)
                    .Take(1)
                    .Select(embedding => new ContentChunk(
                        CollectionPath: collectionPath,
                        FilePath: filePath,
                        ChunkIndex: index,
                        Text: chunkText,
                        ContentHash: hash,
                        Embedding: embedding)))
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
