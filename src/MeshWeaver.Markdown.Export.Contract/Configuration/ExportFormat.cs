namespace MeshWeaver.Markdown.Export.Configuration;

/// <summary>
/// The output format for a document export.
/// </summary>
public enum ExportFormat
{
    /// <summary>PDF, printed from CSS Paged Media by the headless browser.</summary>
    Pdf,

    /// <summary>Microsoft Word .docx via DocumentFormat.OpenXml.</summary>
    Docx,

    /// <summary>
    /// Self-contained, email-client-safe HTML: inline CSS only, table-based layout, absolute
    /// URLs, no script — and, unlike <see cref="Pdf"/>/<see cref="Docx"/>, embedded LIVE LAYOUT
    /// AREAS resolved to static markup (see <c>Html/AreaMarkupRenderer</c>). Produced by
    /// <c>Templates/Export/Html</c>; downloadable as a file and usable directly as the BODY of an
    /// email rather than an attachment.
    /// </summary>
    Html
}
