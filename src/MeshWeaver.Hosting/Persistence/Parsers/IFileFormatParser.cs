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
    /// Parses file content into a MeshNode. Pure in-memory work — synchronous by contract
    /// (the <c>content</c> string is already read; there is no I/O to await or cancel).
    /// </summary>
    /// <param name="filePath">Full path to the file.</param>
    /// <param name="content">File content.</param>
    /// <param name="relativePath">Path relative to the data root (used for namespace/id derivation).</param>
    /// <returns>Parsed MeshNode or null if parsing fails.</returns>
    MeshNode? Parse(string filePath, string content, string relativePath);

    /// <summary>
    /// Serializes a MeshNode back to file content. Pure in-memory work — synchronous.
    /// </summary>
    /// <param name="node">The node to serialize.</param>
    /// <returns>File content string.</returns>
    string Serialize(MeshNode node);

    /// <summary>
    /// Determines if this parser should handle the given node for writing.
    /// </summary>
    /// <param name="node">The node to check.</param>
    /// <returns>True if this parser should handle serialization.</returns>
    bool CanSerialize(MeshNode node);
}
