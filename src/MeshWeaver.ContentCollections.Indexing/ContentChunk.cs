using System.Collections.Immutable;

namespace MeshWeaver.ContentCollections.Indexing;

/// <summary>
/// One indexed window of text extracted from a content file, together with its embedding.
/// Immutable: a chunk is produced once by <see cref="ContentIndexingService"/> and stored
/// verbatim in an <see cref="IChunkedContentVectorStore"/>.
/// </summary>
/// <param name="CollectionPath">Path of the owning content collection (storage-agnostic key).</param>
/// <param name="FilePath">Path of the source file within the collection.</param>
/// <param name="ChunkIndex">Zero-based position of this chunk within the file's chunk sequence.</param>
/// <param name="Text">The chunk text (a character window of the extracted document text).</param>
/// <param name="ContentHash">
/// SHA-256 (hex) of the WHOLE source file's bytes — identical across every chunk of one file.
/// This is the value the hash gate compares to decide whether the file changed since it was
/// last indexed. (Per-chunk identity is <see cref="ChunkIndex"/>, not this.)
/// </param>
/// <param name="Embedding">
/// The embedding vector for <see cref="Text"/>, or null if the chunk has not been embedded yet.
/// </param>
/// <param name="Metadata">Optional free-form metadata (e.g. source content-type).</param>
/// <param name="Page">
/// One-based source page the chunk begins on, or null when the source format carries no layout
/// (txt/markdown/docx) or the page could not be resolved. Provenance for citing/marking the source.
/// </param>
/// <param name="Position">
/// The chunk's normalized bounding box on <see cref="Page"/> (fractions of the page, top-left origin), or
/// null when unknown. Lets a viewer open the source page and mark the exact region the chunk came from.
/// </param>
public sealed record ContentChunk(
    string CollectionPath,
    string FilePath,
    int ChunkIndex,
    string Text,
    string ContentHash,
    float[]? Embedding,
    ImmutableDictionary<string, string>? Metadata = null,
    int? Page = null,
    ChunkPosition? Position = null);
