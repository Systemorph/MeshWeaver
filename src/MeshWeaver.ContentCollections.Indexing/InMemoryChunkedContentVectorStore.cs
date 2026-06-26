using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;

namespace MeshWeaver.ContentCollections.Indexing;

/// <summary>
/// In-memory <see cref="IChunkedContentVectorStore"/> for the unit-tested core. State lives in
/// INSTANCE <see cref="ConcurrentDictionary{TKey,TValue}"/> fields (never static — process-wide
/// static state bleeds across tests). Reactive throughout: every method returns a cold observable
/// that performs its work on subscribe.
/// </summary>
public sealed class InMemoryChunkedContentVectorStore : IChunkedContentVectorStore
{
    // (collectionPath, filePath) -> the file's recorded content hash.
    private readonly ConcurrentDictionary<(string Collection, string File), string> _fileHashes = new();

    // (collectionPath, filePath) -> the file's ordered chunks (the whole set, replaced atomically).
    private readonly ConcurrentDictionary<(string Collection, string File), ImmutableArray<ContentChunk>> _chunks = new();

    /// <summary>Returns the recorded content hash for a file, or <c>null</c> when the file has none.</summary>
    /// <param name="collectionPath">The collection the file belongs to.</param>
    /// <param name="filePath">The file's path within the collection.</param>
    /// <returns>A cold observable emitting the stored hash, or <c>null</c> if the file is not recorded.</returns>
    public IObservable<string?> GetFileHash(string collectionPath, string filePath) =>
        Observable.Defer(() =>
            Observable.Return(
                _fileHashes.TryGetValue((collectionPath, filePath), out var hash) ? hash : null));

    /// <summary>
    /// Atomically replaces all stored chunks for a file with the supplied set and updates the file's
    /// recorded hash (an empty set clears both the chunks and the hash).
    /// </summary>
    /// <param name="collectionPath">The collection the file belongs to.</param>
    /// <param name="filePath">The file's path within the collection.</param>
    /// <param name="chunks">The new ordered chunk set; pass an empty list to remove the file's chunks.</param>
    /// <returns>A cold observable that performs the replacement on subscribe and emits a single <see cref="Unit"/>.</returns>
    public IObservable<Unit> ReplaceFileChunks(
        string collectionPath, string filePath, IReadOnlyList<ContentChunk> chunks) =>
        Observable.Defer(() =>
        {
            var key = (collectionPath, filePath);
            var snapshot = chunks as ContentChunk[] ?? chunks.ToArray();

            // Delete-then-insert: the new set wholly replaces any prior chunks for this file.
            _chunks[key] = ImmutableArray.Create(snapshot);

            // Record the file hash so the next GetFileHash hits the gate. Every chunk of a file
            // carries the same whole-file ContentHash; take the first (empty set clears the hash).
            if (snapshot.Length > 0)
                _fileHashes[key] = snapshot[0].ContentHash;
            else
                _fileHashes.TryRemove(key, out _);

            return Observable.Return(Unit.Default);
        });

    /// <summary>
    /// Ranks the collection's chunks by cosine similarity to the query vector and returns the top matches.
    /// </summary>
    /// <param name="collectionPath">The collection to search within.</param>
    /// <param name="query">The query embedding vector.</param>
    /// <param name="topK">The maximum number of chunks to return, highest similarity first.</param>
    /// <returns>A cold observable emitting the ranked chunks (at most <paramref name="topK"/>).</returns>
    public IObservable<IReadOnlyList<ContentChunk>> Search(
        string collectionPath, float[] query, int topK) =>
        Observable.Defer(() =>
        {
            var ranked = _chunks
                .Where(kvp => kvp.Key.Collection == collectionPath)
                .SelectMany(kvp => kvp.Value)
                .Where(chunk => chunk.Embedding is { Length: > 0 })
                .Select(chunk => (chunk, score: CosineSimilarity(query, chunk.Embedding!)))
                .OrderByDescending(x => x.score)
                .Take(topK)
                .Select(x => x.chunk)
                .ToArray();

            return Observable.Return<IReadOnlyList<ContentChunk>>(ranked);
        });

    /// <summary>Returns a single chunk of a file by its chunk index, or <c>null</c> when not found.</summary>
    /// <param name="collectionPath">The collection the file belongs to.</param>
    /// <param name="filePath">The file's path within the collection.</param>
    /// <param name="chunkIndex">The zero-based index of the chunk to retrieve.</param>
    /// <returns>A cold observable emitting the matching <see cref="ContentChunk"/>, or <c>null</c> if absent.</returns>
    public IObservable<ContentChunk?> GetChunk(string collectionPath, string filePath, int chunkIndex) =>
        Observable.Defer(() =>
        {
            // The chunk set is stored ordered by ChunkIndex (it is built that way), but read it by
            // ChunkIndex rather than by position so a non-contiguous set can never mis-resolve.
            ContentChunk? chunk = null;
            if (chunkIndex >= 0
                && _chunks.TryGetValue((collectionPath, filePath), out var chunks))
                chunk = chunks.FirstOrDefault(c => c.ChunkIndex == chunkIndex);
            return Observable.Return(chunk);
        });

    /// <summary>Returns the number of chunks stored for a file (0 when the file has none).</summary>
    /// <param name="collectionPath">The collection the file belongs to.</param>
    /// <param name="filePath">The file's path within the collection.</param>
    /// <returns>A cold observable emitting the stored chunk count for the file.</returns>
    public IObservable<int> GetChunkCount(string collectionPath, string filePath) =>
        Observable.Defer(() =>
            Observable.Return(
                _chunks.TryGetValue((collectionPath, filePath), out var chunks) ? chunks.Length : 0));

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0)
            return 0d;

        double dot = 0d, normA = 0d, normB = 0d;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * (double)b[i];
            normA += a[i] * (double)a[i];
            normB += b[i] * (double)b[i];
        }

        if (normA == 0d || normB == 0d)
            return 0d;

        return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }
}
