using System.Reactive.Linq;
using MeshWeaver.Markdown.Export.Ast;
using MeshWeaver.Markdown.Export.Branding;
using MeshWeaver.Markdown.Export.Configuration;
using MeshWeaver.Markdown.Export.Docx;
using MeshWeaver.Markdown.Export.Messaging;
using MeshWeaver.Markdown.Export.Pdf;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Markdown.Export.Handlers;

/// <summary>
/// Handles <see cref="ExportDocumentRequest"/> by loading the source markdown (and optional descendants),
/// resolving branding, building the document model, and running the appropriate renderer.
/// </summary>
public static class ExportDocumentHandler
{
    /// <summary>
    /// Registers the handler on a hub configuration. Registered inside <c>AddMarkdownExport()</c>.
    /// </summary>
    public static MessageHubConfiguration AddExportDocumentHandler(this MessageHubConfiguration config)
        => config
            .WithTypes(
                typeof(ExportDocumentRequest),
                typeof(ExportDocumentResponse),
                typeof(DocumentExportOptions))
            .WithHandler<ExportDocumentRequest>(HandleAsync);

    private static async Task<IMessageDelivery> HandleAsync(
        IMessageHub hub, IMessageDelivery<ExportDocumentRequest> delivery, CancellationToken ct)
    {
        var logger = hub.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(ExportDocumentHandler).FullName!);
        var request = delivery.Message;

        try
        {
            var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
            var node = await meshService.QueryAsync<MeshNode>($"path:{request.SourcePath}").FirstOrDefaultAsync();
            if (node is null)
            {
                hub.Post(new ExportDocumentResponse(
                    request.Options.Format,
                    "error.txt",
                    "text/plain",
                    System.Text.Encoding.UTF8.GetBytes($"Source node not found: {request.SourcePath}")),
                    o => o.ResponseFor(delivery));
                return delivery.Processed();
            }

            var title = request.Options.Title ?? node.Name ?? node.Id;

            var branding = await hub.ServiceProvider.GetRequiredService<BrandingResolver>()
                .ResolveAsync(request.Options.BrandNodePath, ct);

            // Collect chapters: primary node + optional descendants
            var chapters = new List<(string, string)>();
            chapters.Add((title, ExtractMarkdown(node)));

            if (request.Options.IncludeChildren)
            {
                await foreach (var desc in meshService.QueryAsync<MeshNode>(
                    $"path:{request.SourcePath} scope:descendants").WithCancellation(ct))
                {
                    var md = ExtractMarkdown(desc);
                    if (!string.IsNullOrWhiteSpace(md))
                        chapters.Add((desc.Name ?? desc.Id, md));
                }
            }

            var document = new DocumentBuilder().Build(title, chapters, request.Options, branding);

            byte[] bytes;
            string mime;
            string extension;
            switch (request.Options.Format)
            {
                case ExportFormat.Pdf:
                    bytes = new PdfDocumentRenderer().Render(document);
                    mime = "application/pdf";
                    extension = "pdf";
                    break;
                case ExportFormat.Docx:
                    bytes = new DocxDocumentRenderer().Render(document);
                    mime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
                    extension = "docx";
                    break;
                default:
                    throw new NotSupportedException($"Unsupported format {request.Options.Format}");
            }

            var fileName = $"{Sanitize(title)}.{extension}";
            hub.Post(new ExportDocumentResponse(request.Options.Format, fileName, mime, bytes),
                o => o.ResponseFor(delivery));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Export failed for {Path}", request.SourcePath);
            hub.Post(new ExportDocumentResponse(
                request.Options.Format,
                "error.txt",
                "text/plain",
                System.Text.Encoding.UTF8.GetBytes($"Export failed: {ex.Message}")),
                o => o.ResponseFor(delivery));
        }

        return delivery.Processed();
    }

    private static string ExtractMarkdown(MeshNode node)
    {
        if (node.Content is MeshWeaver.Markdown.MarkdownContent mc)
            return mc.Content ?? "";
        if (node.Content is string s) return s;
        return "";
    }

    private static string Sanitize(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder();
        foreach (var c in s)
            sb.Append(invalid.Contains(c) ? '_' : c);
        var name = sb.ToString().Trim();
        return string.IsNullOrEmpty(name) ? "Document" : name;
    }
}
