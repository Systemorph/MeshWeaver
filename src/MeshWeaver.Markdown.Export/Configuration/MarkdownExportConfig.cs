namespace MeshWeaver.Markdown.Export.Configuration;

/// <summary>
/// Destination settings for generated export files. The handler writes the rendered
/// PDF / DOCX into the named content collection under the given directory, and the
/// client receives a link back (no byte streaming through the hub).
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
