using System.ComponentModel;
using System.Reactive.Linq;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Markdown.Export.Configuration;
using MeshWeaver.Markdown.Export.Messaging;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Markdown.Export.Layout;

/// <summary>
/// Layout areas that perform the markdown export and render a markdown control with a
/// download link to the generated file. Two entry points: <c>ExportPdf</c> and <c>ExportDocx</c>.
/// Avoids the heavy <c>ExportDocumentControl</c> dialog — the portal just pops a link.
/// </summary>
[Browsable(false)]
public static class ExportDocumentLayoutArea
{
    public const string PdfArea = "ExportPdf";
    public const string DocxArea = "ExportDocx";

    [Browsable(false)]
    public static IObservable<UiControl?> RenderPdf(LayoutAreaHost host, RenderingContext ctx) =>
        RenderExport(host, ExportFormat.Pdf);

    [Browsable(false)]
    public static IObservable<UiControl?> RenderDocx(LayoutAreaHost host, RenderingContext ctx) =>
        RenderExport(host, ExportFormat.Docx);

    private static IObservable<UiControl?> RenderExport(LayoutAreaHost host, ExportFormat format)
    {
        var hubPath = host.Hub.Address.ToString();
        var cfg = host.Hub.ServiceProvider.GetRequiredService<MarkdownExportConfig>();
        var request = new ExportDocumentRequest(hubPath,
            new DocumentExportOptions { Format = format });

        // Kick off the export via the hub handler and map the response to a markdown link.
        return Observable
            .FromAsync(async () =>
            {
                var delivery = await host.Hub.AwaitResponse(
                    request, o => o.WithTarget(host.Hub.Address));
                return delivery.Message;
            })
            .Select(response => (UiControl?)BuildResultMarkdown(response, format, hubPath, cfg))
            .StartWith(BuildPendingMarkdown(format));
    }

    private static UiControl BuildPendingMarkdown(ExportFormat format)
    {
        var formatLabel = System.Net.WebUtility.HtmlEncode(format.ToString().ToUpperInvariant());
        return Controls.Html(
            $"<div style=\"padding:16px;\">" +
            $"<p><strong>Preparing {formatLabel} export…</strong></p>" +
            $"<p>The document is being rendered. The download link will appear here when it's ready.</p>" +
            $"</div>");
    }

    private static UiControl BuildResultMarkdown(
        ExportDocumentResponse response, ExportFormat format, string hubPath, MarkdownExportConfig cfg)
    {
        var formatLabel = System.Net.WebUtility.HtmlEncode(format.ToString().ToUpperInvariant());
        if (!string.IsNullOrEmpty(response.Error))
        {
            var err = System.Net.WebUtility.HtmlEncode(response.Error);
            return Controls.Html(
                $"<div style=\"padding:16px;color:var(--warning-color);\">" +
                $"<p><strong>Export failed</strong></p>" +
                $"<p>{err}</p>" +
                $"</div>");
        }

        var url = BuildDownloadUrl(response.ContentPath, hubPath, cfg.CollectionName);
        var fileName = System.Net.WebUtility.HtmlEncode(response.FileName);
        var contentPath = System.Net.WebUtility.HtmlEncode(response.ContentPath);
        var href = System.Net.WebUtility.HtmlEncode(url);

        return Controls.Html(
            $"<div style=\"padding:16px;\">" +
            $"<p><strong>Your {formatLabel} is ready</strong></p>" +
            $"<p><a href=\"{href}\" target=\"_blank\" rel=\"noopener\" " +
                $"style=\"display:inline-flex;align-items:center;gap:6px;\">" +
                $"⬇ Download {fileName}</a></p>" +
            $"<p style=\"font-size:0.85rem;color:var(--neutral-foreground-hint);\">Saved to <code>{contentPath}</code>.</p>" +
            $"</div>");
    }

    /// <summary>
    /// Resolves a <c>content:{pathWithinCollection}</c> reference into a portal-relative URL
    /// that the static-content endpoint (<c>/static/{nodeAddress}/{collection}/{path}</c>,
    /// see <c>BlazorHostingExtensions.MapStaticContent</c>) can serve to the browser.
    /// </summary>
    private static string BuildDownloadUrl(string contentPath, string hubPath, string collectionName)
    {
        const string prefix = "content:";
        if (string.IsNullOrEmpty(contentPath) || !contentPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return contentPath;

        var rest = contentPath[prefix.Length..].TrimStart('/');
        // URL-encode each path segment so spaces and special chars in file names don't break.
        var encodedRest = string.Join("/", rest.Split('/').Select(Uri.EscapeDataString));
        return $"/static/{hubPath}/{collectionName}/{encodedRest}";
    }
}
