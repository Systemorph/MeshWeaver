using MeshWeaver.Markdown.Export.Configuration;

namespace MeshWeaver.Markdown.Export.Messaging;

/// <summary>
/// Response indicating where the rendered document was saved. The handler writes the
/// file into a content collection (see <see cref="MarkdownExportConfig"/>) and returns
/// a content-relative path; the client resolves it to a download link via the portal.
/// </summary>
/// <param name="Format">Format emitted.</param>
/// <param name="FileName">Final file name including extension.</param>
/// <param name="MimeType">Mime type of the produced file.</param>
/// <param name="ContentPath">
/// Content reference in the form <c>content:{collection}/{path}</c> that the client can
/// resolve to a download URL or link target. Empty string on failure.
/// </param>
/// <param name="Error">Error message when the export failed; <c>null</c> on success.</param>
public record ExportDocumentResponse(
    ExportFormat Format,
    string FileName,
    string MimeType,
    string ContentPath,
    string? Error = null);
