namespace MeshWeaver.Markdown.Export.Configuration;

/// <summary>
/// Configuration for the markdown export pipeline. The current export flow renders
/// the PDF / DOCX payload and returns it directly to the client for download.
/// </summary>
public class MarkdownExportConfig
{
    /// <summary>
    /// Name of the content collection to write exports into. Defaults to <c>content</c>
    /// — the single editable collection every node hub has by default.
    /// </summary>
    public string CollectionName { get; set; } = "content";

    /// <summary>
    /// Sub-directory within the collection (relative to the collection root) where
    /// exports land. Defaults to <c>Export</c>. Created implicitly by the file provider.
    /// </summary>
    public string ExportDirectory { get; set; } = "Export";

    /// <summary>
    /// When <c>true</c> (the default), re-exporting the same source node replaces the
    /// existing file at the destination. When <c>false</c>, the filename gets a numeric
    /// suffix to avoid clobbering prior exports.
    /// </summary>
    public bool Overwrite { get; set; } = true;
}
