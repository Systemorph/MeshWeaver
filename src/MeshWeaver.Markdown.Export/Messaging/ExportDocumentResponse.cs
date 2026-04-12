using MeshWeaver.Markdown.Export.Configuration;

namespace MeshWeaver.Markdown.Export.Messaging;

/// <summary>
/// Response carrying the rendered document bytes back to the caller.
/// </summary>
/// <param name="Format">Format emitted.</param>
/// <param name="FileName">Suggested file name including extension, ready for browser download.</param>
/// <param name="MimeType">Mime type of the produced file.</param>
/// <param name="Content">Raw bytes of the produced file.</param>
public record ExportDocumentResponse(
    ExportFormat Format,
    string FileName,
    string MimeType,
    byte[] Content);
