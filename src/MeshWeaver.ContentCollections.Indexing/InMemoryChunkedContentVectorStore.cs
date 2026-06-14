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

    public IObservable<string?> GetFileHash(string collectionPath, string filePath) =>
        Observable.Defer(() =>
            Observable.Return(
                _fileHashes.TryGetValue((collectionPath, filePath), out var hash) ? hash : null));

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
