using MeshWeaver.Markdown.Export.Configuration;

namespace MeshWeaver.Markdown.Export.Messaging;

/// <summary>
/// Value record the export script templates return. Travels through
/// <c>ActivityLog.ReturnValue</c> as JSON; subscribers deserialize it on the
/// activity's terminal snapshot to get the rendered bytes + file metadata.
///
/// <para>Plain value type — no <c>IRequest</c>/<c>IResponse</c> coupling, no
/// hub round-trip. Carries everything a downloader needs (file name, mime,
/// bytes) so the Blazor view / MCP tool / test can <c>InvokeVoidAsync</c>
/// the browser download or write to a content collection without further
/// lookups.</para>
/// </summary>
/// <param name="Format">Format that was rendered.</param>
/// <param name="FileName">Suggested file name with extension.</param>
/// <param name="MimeType">Mime type of the produced file.</param>
/// <param name="Content">Raw bytes.</param>
public record RenderedDocument(
    ExportFormat Format,
    string FileName,
    string MimeType,
    byte[] Content);
