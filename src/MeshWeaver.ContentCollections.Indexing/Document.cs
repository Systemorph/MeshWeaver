using MeshWeaver.Domain;
using MeshWeaver.Mesh;

namespace MeshWeaver.ContentCollections.Indexing;

/// <summary>
/// Content of the framework-generic <c>Document</c> NodeType: one indexed source file's
/// AI summary plus its indexing metadata. A real <see cref="IDocumentSink"/> (in a hosting
/// project) creates/updates a mesh node with this as its <c>Content</c>, mapping
/// <see cref="Name"/> to the node's <c>Name</c>.
/// </summary>
/// <remarks>
/// TODO (hosting): the actual <c>Document</c> NodeType definition (registration via
/// <c>AddMeshNodes</c>/a NodeType class) and the real <see cref="IDocumentSink"/> that writes it
/// through <c>workspace.GetMeshNodeStream(path).Update(...)</c> land in a hosting project later —
/// they are intentionally out of scope for the storage-agnostic indexing core. This record only
/// fixes the content shape so the sink and the future NodeType agree.
/// </remarks>
public sealed record Document
{
    /// <summary>The document title (the source file name). Maps to the mesh node's <c>Name</c>.</summary>
    [MeshNodeProperty(nameof(MeshNode.Name))]
    public string Name { get; init; } = string.Empty;

    /// <summary>The AI-produced summary of the document's text (the node body).</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>Path of the owning content collection.</summary>
    public string CollectionPath { get; init; } = string.Empty;

    /// <summary>Path of the source file within the collection (the source reference).</summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>Optional content-type of the source file.</summary>
    public string? Mime { get; init; }

    /// <summary>Size of the source file in bytes.</summary>
    public long SizeBytes { get; init; }

    /// <summary>SHA-256 (hex) of the whole source file's bytes — the value the hash gate compares.</summary>
    public string ContentHash { get; init; } = string.Empty;

    /// <summary>Number of chunks the file was split into and stored.</summary>
    public int ChunkCount { get; init; }

    /// <summary>When the file was indexed (this <c>Document</c> last written).</summary>
    public DateTimeOffset IndexedAt { get; init; }
}
