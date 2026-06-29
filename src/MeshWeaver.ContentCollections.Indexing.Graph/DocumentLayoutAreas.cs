using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;

namespace MeshWeaver.ContentCollections.Indexing.Graph;

/// <summary>
/// Custom views for <c>Document</c> nodes (the indexed-file summary nodes written by
/// <see cref="MeshDocumentSink"/>). The Overview renders the AI summary as markdown plus a compact
/// metadata row (source file link, mime, size, chunk count, indexed-at).
/// </summary>
public static class DocumentLayoutAreas
{
    /// <summary>
    /// Overview for a <c>Document</c> node. Reactive — reads the node off the per-node hub's own
    /// stream (<c>host.Workspace.GetMeshNodeStream()</c>) and re-renders on every change.
    /// </summary>
    public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext _)
        => host.Workspace.GetMeshNodeStream()
            .Select(node => (UiControl?)BuildOverview(host, node));

    private const string ContainerStyle = "max-width: 1280px; margin: 0 auto; padding: 24px; gap: 12px;";

    private static UiControl BuildOverview(LayoutAreaHost host, MeshNode? node)
    {
        var container = Controls.Stack
            .WithWidth("100%")
            .WithStyle(ContainerStyle);

        var title = node?.Name ?? node?.Path ?? "Document";
        container = container.WithView(Controls.Html(
            $"<h1 style=\"margin: 0; font-size: 1.75rem; font-weight: 600;\">{EscapeHtml(title)}</h1>"));

        var document = node?.ContentAs<Document>(host.Hub.JsonSerializerOptions);
        if (document is null)
        {
            container = container.WithView(Controls.Html(
                "<p style=\"color: var(--neutral-foreground-hint); font-style: italic;\">No document data.</p>"));
            return container;
        }

        // The AI summary is the document body — rendered as markdown.
        if (!string.IsNullOrWhiteSpace(document.Summary))
            container = container.WithView(Controls.Markdown(document.Summary));
        else
            container = container.WithView(Controls.Html(
                "<p style=\"color: var(--neutral-foreground-hint); font-style: italic;\">No summary available.</p>"));

        container = container.WithView(BuildMetadata(document));
        return container;
    }

    /// <summary>
    /// Compact metadata row: source-file link, mime, size, chunk count, indexed-at. The source link
    /// points at the file's path within the collection (display + navigation reference).
    /// </summary>
    private static UiControl BuildMetadata(Document document)
    {
        var details = Controls.Stack.WithWidth("100%").WithStyle(
            "gap: 4px; margin-top: 16px; padding-top: 12px; border-top: 1px solid var(--neutral-stroke-rest); " +
            "font-size: 0.85rem; color: var(--neutral-foreground-hint);");

        // Source file: link to the file within the collection. FilePath is the source reference;
        // we surface it as a navigable link to the collection-relative path.
        var sourceHref = BuildSourceHref(document.CollectionPath, document.FilePath);
        var fileName = string.IsNullOrEmpty(document.Name) ? document.FilePath : document.Name;
        details = details.WithView(Controls.Html(
            $"<div><strong>Source:</strong> <a href=\"{Encode(sourceHref)}\">{EscapeHtml(fileName)}</a></div>"));

        if (!string.IsNullOrEmpty(document.Mime))
            details = details.WithView(Row("Type", document.Mime));

        details = details.WithView(Row("Size", FormatBytes(document.SizeBytes)));
        details = details.WithView(Row("Chunks", document.ChunkCount.ToString()));

        if (document.IndexedAt != default)
            details = details.WithView(Row("Indexed", document.IndexedAt.ToString("yyyy-MM-dd HH:mm 'UTC'")));

        return details;
    }

    private static UiControl Row(string label, string value)
        => Controls.Html($"<div><strong>{EscapeHtml(label)}:</strong> {EscapeHtml(value)}</div>");

    /// <summary>
    /// Builds the navigable href for the source file. The file lives within the content collection,
    /// so the link is the collection-relative file path. Leading slash makes it an absolute portal
    /// path; we never url-encode the mesh separators (see CLAUDE.md "Mesh URL Shape").
    /// </summary>
    private static string BuildSourceHref(string collectionPath, string filePath)
    {
        var collection = collectionPath.Trim('/');
        var file = filePath.Replace('\\', '/').TrimStart('/');
        return string.IsNullOrEmpty(collection) ? $"/{file}" : $"/{collection}/{file}";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double value = bytes;
        string[] units = ["KB", "MB", "GB", "TB"];
        var unit = -1;
        do
        {
            value /= 1024;
            unit++;
        } while (value >= 1024 && unit < units.Length - 1);
        return $"{value:0.#} {units[unit]}";
    }

    private static string EscapeHtml(string? text) => System.Net.WebUtility.HtmlEncode(text ?? "");

    private static string Encode(string? url) => System.Web.HttpUtility.HtmlAttributeEncode(url ?? "");
}
