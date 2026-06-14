using System.Reactive;

namespace MeshWeaver.ContentCollections.Indexing;

/// <summary>
/// Storage-agnostic sink + query surface for indexed content chunks. The in-memory
/// implementation backs the unit-tested core; a Postgres/pgvector adapter layers on this exact
/// contract later without changing the orchestration.
/// </summary>
public interface IChunkedContentVectorStore
{
    /// <summary>
    /// Returns the content hash recorded for <paramref name="filePath"/> the last time it was
    /// indexed, or null if the file has never been indexed. The hash gate compares this to the
    /// current file hash to skip unchanged files.
    /// </summary>
    IObservable<string?> GetFileHash(string collectionPath, string filePath);

    /// <summary>
    /// Replaces ALL chunks for <paramref name="filePath"/> with <paramref name="chunks"/>
    /// (delete-then-insert) and records the file's content hash for the next hash-gate check.
    /// Idempotent: re-running with the same chunks leaves the store in the same state.
    /// </summary>
    IObservable<Unit> ReplaceFileChunks(
        string collectionPath, string filePath, IReadOnlyList<ContentChunk> chunks);

    /// <summary>
    /// Returns the <paramref name="topK"/> chunks in <paramref name="collectionPath"/> most
    /// similar to <paramref name="query"/> by cosine similarity, most-similar first.
    /// </summary>
    IObservable<IReadOnlyList<ContentChunk>> Search(string collectionPath, float[] query, int topK);
}
