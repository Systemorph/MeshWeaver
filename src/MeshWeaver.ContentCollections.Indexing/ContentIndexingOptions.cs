namespace MeshWeaver.ContentCollections.Indexing;

/// <summary>
/// Tuning for <see cref="ContentIndexingService"/>. Immutable record so a host can override any
/// field via <c>options with { ChunkSize = 2000 }</c> without a new type.
/// </summary>
public sealed record ContentIndexingOptions
{
    /// <summary>Character-window size handed to <see cref="TextChunker.Chunk"/>. Defaults to 1000.</summary>
    public int ChunkSize { get; init; } = 1000;

    /// <summary>Character overlap between consecutive chunks. Defaults to 150.</summary>
    public int ChunkOverlap { get; init; } = 150;

    /// <summary>
    /// Max chunks embedded concurrently (the bounded merge degree). The embedder's own
    /// <c>IIoPool</c> still governs the real network/compute concurrency; this only caps how many
    /// embed observables one file's indexing subscribes to at once. Defaults to 8.
    /// </summary>
    public int MaxConcurrentEmbeddings { get; init; } = 8;
}
