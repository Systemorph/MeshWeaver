using MeshWeaver.Markdown.Export.Configuration;
using MeshWeaver.Messaging;

namespace MeshWeaver.Markdown.Export.Messaging;

/// <summary>
/// Request to export a markdown node (and optionally its descendants) as PDF or DOCX.
/// Handled by the markdown node's hub; returns an <see cref="ExportDocumentResponse"/>.
/// </summary>
/// <param name="SourcePath">Path of the source markdown node, e.g. <c>Doc/Architecture/MessageHub</c>.</param>
/// <param name="Options">User-selected export options (format, branding, page breaks, TOC, include children).</param>
public record ExportDocumentRequest(string SourcePath, DocumentExportOptions Options) : IRequest<ExportDocumentResponse>;
