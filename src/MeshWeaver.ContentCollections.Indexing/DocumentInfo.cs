namespace MeshWeaver.ContentCollections.Indexing;

/// <summary>
/// The per-file record handed to an <see cref="IDocumentSink"/> after a changed file has been
/// extracted, summarized and chunked. Immutable; carries everything a <c>Document</c> mesh-node
/// instance needs (title = <see cref="FileName"/>, the AI <see cref="Summary"/>, the source path
/// and the indexing metadata).
/// </summary>
/// <param name="CollectionPath">Path of the owning content collection.</param>
/// <param name="FilePath">Path of the source file within the collection (the source reference).</param>
/// <param name="FileName">Display name / title of the file (the document's <c>Name</c>).</param>
/// <param name="Summary">The AI-produced summary of the document's text.</param>
/// <param name="ContentHash">SHA-256 (hex) of the whole source file's bytes — the same hash the gate compares.</param>
/// <param name="Mime">Optional content-type of the source file, when the host knows it.</param>
/// <param name="SizeBytes">Size of the source file in bytes.</param>
/// <param name="ChunkCount">Number of chunks the file was split into and stored.</param>
public sealed record DocumentInfo(
    string CollectionPath,
    string FilePath,
    string FileName,
    string Summary,
    string ContentHash,
    string? Mime,
    long SizeBytes,
    int ChunkCount);
