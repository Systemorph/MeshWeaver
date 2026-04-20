using MeshWeaver.Markdown.Export.Configuration;

namespace MeshWeaver.Markdown.Export.Messaging;

/// <summary>
/// Response carrying the rendered document bytes back to the caller.
/// </summary>
/// <param name="Format">Format emitted.</param>
/// <param name="FileName">Suggested file name including extension.</param>
/// <param name="MimeType">Mime type of the produced file.</param>
/// <param name="Content">Raw bytes of the produced file; empty when <see cref="Error"/> is set.</param>
/// <param name="Error">Error message when the export failed; <c>null</c> on success.</param>
public record ExportDocumentResponse(
    ExportFormat Format,
    string FileName,
    string MimeType,
    byte[] Content,
    string? Error = null);
