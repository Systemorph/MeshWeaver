using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Data;
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
/// Sync handler — composes via <c>IObservable</c> + <c>Subscribe</c>; no <c>await</c>.
/// </summary>
public static class ExportDocumentHandler
{
    /// <summary>
    /// Registers the handler on a hub configuration. Registered inside <c>AddMarkdownExport()</c>.
    /// </summary>
    public static MessageHubConfiguration AddExportDocumentHandler(this MessageHubConfiguration config)
    {
        config.TypeRegistry.AddMarkdownExportTypes();
        return config.WithHandler<ExportDocumentRequest>(Handle);
    }

    private static IMessageDelivery Handle(
        IMessageHub hub, IMessageDelivery<ExportDocumentRequest> delivery)
    {
        var logger = hub.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(ExportDocumentHandler).FullName!);
        var request = delivery.Message;
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var brandingResolver = hub.ServiceProvider.GetRequiredService<BrandingResolver>();

        // Read root → resolve branding + collect descendants in parallel → build → respond.
        // Each step in the chain is a Subscribe callback; the handler returns synchronously.
        // Root read is one-shot — GetDataRequest, not a SubscribeRequest+unsubscribe.
        hub.GetMeshNode(request.SourcePath, TimeSpan.FromSeconds(15))
            .SelectMany(rootNode =>
            {
                if (rootNode is null)
                {
                    hub.Post(new ExportDocumentResponse(
                            request.Options.Format, "", "", Array.Empty<byte>(),
                            Error: $"Source node not found: {request.SourcePath}"),
                        o => o.ResponseFor(delivery));
                    return Observable.Empty<Unit>();
                }

                var title = request.Options.Title ?? rootNode.Name ?? rootNode.Id;

                var brandingObs = brandingResolver.Resolve(request.Options.BrandNodePath);

                var chaptersObs = CollectChapters(meshService, request, rootNode, title);

                return brandingObs.Zip(chaptersObs,
                    (branding, chapters) => RenderAndPost(hub, delivery, request, title, chapters, branding));
            })
            .Subscribe(
                _ => { },
                ex =>
                {
                    logger.LogError(ex, "Export failed for {Path}", request.SourcePath);
                    hub.Post(new ExportDocumentResponse(
                            request.Options.Format, "", "", Array.Empty<byte>(),
                            Error: ex.Message),
                        o => o.ResponseFor(delivery));
                });

        return delivery.Processed();
    }

    private static IObservable<List<(string, string)>> CollectChapters(
        IMeshService meshService, ExportDocumentRequest request, MeshNode rootNode, string title) =>
        Observable.FromAsync(async () =>
        {
            var chapters = new List<(string, string)> { (title, ExtractMarkdown(rootNode)) };
            if (!request.Options.IncludeChildren)
                return chapters;

            var maxDepth = request.Options.MaxDepth;
            var rootDepth = request.SourcePath.Count(c => c == '/');
            await foreach (var desc in meshService.QueryAsync<MeshNode>(
                $"path:{request.SourcePath} scope:descendants"))
            {
                if (maxDepth > 0)
                {
                    var depth = desc.Path.Count(c => c == '/') - rootDepth;
                    if (depth > maxDepth) continue;
                }
                var md = ExtractMarkdown(desc);
                if (!string.IsNullOrWhiteSpace(md))
                    chapters.Add((desc.Name ?? desc.Id, md));
            }
            return chapters;
        });

    private static Unit RenderAndPost(
        IMessageHub hub,
        IMessageDelivery<ExportDocumentRequest> delivery,
        ExportDocumentRequest request,
        string title,
        IReadOnlyList<(string, string)> chapters,
        BrandingOptions branding)
    {
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
        return Unit.Default;
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
