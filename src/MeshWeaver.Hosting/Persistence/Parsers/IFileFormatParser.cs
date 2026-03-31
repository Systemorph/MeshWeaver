using MeshWeaver.Mesh;

namespace MeshWeaver.Hosting.Persistence.Parsers;

/// <summary>
/// Interface for parsing different file formats into MeshNode objects.
/// </summary>
public interface IFileFormatParser
{
    /// <summary>
    /// File extensions this parser handles (e.g., ".md", ".cs").
    /// </summary>
    IReadOnlyList<string> SupportedExtensions { get; }

    /// <summary>
    /// Parses file content into a MeshNode.
    /// </summary>
    /// <param name="filePath">Full path to the file.</param>
    /// <param name="content">File content.</param>
    /// <param name="relativePath">Path relative to the data root (used for namespace/id derivation).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Parsed MeshNode or null if parsing fails.</returns>
    Task<MeshNode?> ParseAsync(string filePath, string content, string relativePath, CancellationToken ct = default);

    /// <summary>
    /// Serializes a MeshNode back to file content.
    /// </summary>
    /// <param name="node">The node to serialize.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>File content string.</returns>
    Task<string> SerializeAsync(MeshNode node, CancellationToken ct = default);

    /// <summary>
    /// Determines if this parser should handle the given node for writing.
    /// </summary>
    /// <param name="node">The node to check.</param>
    /// <returns>True if this parser should handle serialization.</returns>
    bool CanSerialize(MeshNode node);
}
