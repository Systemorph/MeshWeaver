namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Response for export operations.
/// </summary>
public record ExportResult
{
    /// <summary>Number of nodes exported.</summary>
    public int NodesExported { get; init; }
    /// <summary>Number of partition files exported.</summary>
    public int PartitionsExported { get; init; }
    /// <summary>Error message if the export failed; null on success.</summary>
    public string? Error { get; init; }
    /// <summary>True if the export succeeded.</summary>
    public bool Success => Error == null;

    public static ExportResult Ok(int nodesExported, int partitionsExported = 0) => new()
    {
        NodesExported = nodesExported,
        PartitionsExported = partitionsExported
    };

    public static ExportResult Fail(string error) => new() { Error = error };
}

/// <summary>
/// Service for exporting mesh nodes to a directory using file persister formats
/// (.md for markdown, .cs for code, .json for others).
/// </summary>
public interface IMeshExportService
{
    /// <summary>
    /// Exports a node subtree to a directory using native file formats.
    /// </summary>
    /// <param name="rootPath">Root path of the subtree to export</param>
    /// <param name="outputDirectory">Target directory to write files to</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Export result with counts</returns>
    Task<ExportResult> ExportToDirectoryAsync(
        string rootPath,
        string outputDirectory,
        CancellationToken ct = default);
}
