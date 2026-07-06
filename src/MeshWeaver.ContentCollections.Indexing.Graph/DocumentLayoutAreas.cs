using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.ContentCollections.Indexing.Graph;

/// <summary>
/// Custom views for <c>Document</c> nodes (the indexed-file summary nodes written by
/// <see cref="MeshDocumentSink"/>). The Overview renders the AI summary as markdown plus a compact
/// metadata row (source file link, mime, size, chunk count, indexed-at); <see cref="Blocks"/> is the
/// content-index-block reader (one chunk at a time, prev/next, jump to the original);
/// <see cref="Source"/> renders the original PDF/DOCX with the matched passage highlighted.
/// </summary>
public static class DocumentLayoutAreas
{
    /// <summary>Area name of the content-index-block reader (<see cref="Blocks"/>).</summary>
    public const string BlocksArea = "Blocks";

    /// <summary>Area name of the original-file viewer (<see cref="Source"/>).</summary>
    public const string SourceArea = "Source";

    // The currently-displayed chunk index (string), driven by the prev/next buttons and seeded from
    // the ?index= query param. Lives on the area's data stream so stepping re-renders the block panel
    // in place — no full-page navigation (mirrors the Content-Indexing tab's explore reader).
    private const string BlockIndexId = "docBlockIndex";

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
        container = container.WithView(BuildActions(node?.Path ?? host.Hub.Address.ToString()));
        return container;
    }

    /// <summary>A row of navigation buttons into the block reader and the original-file viewer.</summary>
    private static UiControl BuildActions(string nodePath)
    {
        var row = Controls.Stack.WithWidth("100%").WithStyle("flex-direction:row; gap:8px; margin-top:12px;");
        row = row.WithView(Controls.Button("Read indexed blocks")
            .WithAppearance(Appearance.Outline)
            .WithClickAction(c => { c.NavigateTo($"/{nodePath}/{BlocksArea}"); return Task.CompletedTask; }));
        row = row.WithView(Controls.Button("Open original")
            .WithAppearance(Appearance.Outline)
            .WithClickAction(c => { c.NavigateTo($"/{nodePath}/{SourceArea}"); return Task.CompletedTask; }));
        return row;
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

    // ── Blocks: the content-index-block reader (one chunk at a time, prev/next, jump to original) ──

    /// <summary>
    /// The content-index-block reader for a <c>Document</c> node. Shows one chunk (content index block)
    /// at a time with the matched passage highlighted, steps prev/next in place (no full navigation),
    /// and links to the original file. Reads <c>?index=</c> (which block) and <c>?q=</c> (terms to
    /// highlight) — the URL the global search routes a content hit to.
    /// </summary>
    public static IObservable<UiControl?> Blocks(LayoutAreaHost host, RenderingContext _)
    {
        var terms = host.GetQueryStringParamValue("q") ?? "";
        // Seed the index ONCE (not inside the node-stream Select) so a node-content emission never
        // resets the reader to the deep-linked block while the user is stepping.
        host.UpdateData(BlockIndexId, ParseIndex(host.GetQueryStringParamValue("index")).ToString());

        return host.Workspace.GetMeshNodeStream()
            .Select(node => (UiControl?)BuildBlocksContainer(host, node, terms));
    }

    private static UiControl BuildBlocksContainer(LayoutAreaHost host, MeshNode? node, string terms)
    {
        var container = Controls.Stack.WithWidth("100%").WithStyle(ContainerStyle);
        container = container.WithView(Controls.H2(node?.Name ?? node?.Path ?? "Document"));

        var document = node?.ContentAs<Document>(host.Hub.JsonSerializerOptions);
        if (document is null)
            return container.WithView(Controls.Markdown("_No document data._"));

        var nodePath = node?.Path ?? host.Hub.Address.ToString();

        // The block panel is bound to the current index data stream — prev/next re-render in place.
        return container.WithView((h, _) => h.Stream.GetDataStream<string>(BlockIndexId)
            .DistinctUntilChanged()
            .Select(idx => BuildBlockPanel(h, document, nodePath, ParseIndex(idx), terms))
            .Switch()
            .StartWith((UiControl?)Controls.Stack.WithWidth("100%")));
    }

    private static IObservable<UiControl?> BuildBlockPanel(
        LayoutAreaHost host, Document document, string nodePath, int index, string terms)
    {
        var store = host.Hub.ServiceProvider.GetService<IChunkedContentVectorStore>();
        if (store is null)
            return Observable.Return((UiControl?)Controls.Markdown(
                "_Content indexing is not enabled on this server._"));

        return store.GetChunkCount(document.CollectionPath, document.FilePath)
            .SelectMany(total => store.GetChunk(document.CollectionPath, document.FilePath, index)
                .Select(chunk => (UiControl?)BuildBlockView(document, nodePath, index, total, chunk, terms)))
            // Surface a generic message to the user; the exception (which may carry internal detail) is
            // logged, not rendered.
            .Catch<UiControl?, Exception>(ex =>
            {
                host.Hub.ServiceProvider.GetService<ILoggerFactory>()?
                    .CreateLogger(typeof(DocumentLayoutAreas))
                    .LogWarning(ex, "Failed to load content block {Index} for {Collection}/{File}",
                        index, document.CollectionPath, document.FilePath);
                return Observable.Return((UiControl?)Controls.Markdown("_Could not load this block._"));
            });
    }

    private static UiControl BuildBlockView(
        Document document, string nodePath, int index, int total, ContentChunk? chunk, string terms)
    {
        var panel = Controls.Stack.WithWidth("100%").WithStyle(
            "gap: 10px; margin-top: 8px; padding: 16px; background: var(--neutral-layer-2); border-radius: 8px;");

        var fileName = string.IsNullOrEmpty(document.Name) ? document.FilePath : document.Name;
        var position = total > 0 ? $"{index + 1} / {total}" : (index + 1).ToString();
        var pageSuffix = chunk?.Page is int page ? $" · page {page}" : "";
        panel = panel.WithView(Controls.H3($"{fileName} · block {position}{pageSuffix}"));

        if (chunk is null)
        {
            panel = panel.WithView(Controls.Markdown(total == 0
                ? "_No blocks indexed for this file._"
                : $"_No block at index {index}; valid range is 0..{total - 1}._"));
            return WithBlockNav(panel, nodePath, index, total, terms);
        }

        // The chunk text with the matched passage highlighted — a framework control, never an HTML string.
        panel = panel.WithView(new HighlightControl(chunk.Text, terms).WithStyle(
            "display:block; font-size:0.9rem; line-height:1.5; max-height:420px; overflow:auto; " +
            "padding:12px; background:var(--neutral-layer-1); border-radius:6px;"));

        return WithBlockNav(panel, nodePath, index, total, terms);
    }

    private static UiControl WithBlockNav(StackControl panel, string nodePath, int index, int total, string terms)
    {
        var nav = Controls.Stack.WithWidth("100%").WithStyle("flex-direction:row; gap:8px; flex-wrap:wrap;");
        if (index > 0)
            nav = nav.WithView(Controls.Button("← Previous").WithAppearance(Appearance.Outline)
                .WithClickAction(c => { c.Host.UpdateData(BlockIndexId, (index - 1).ToString()); return Task.CompletedTask; }));
        if (total <= 0 || index < total - 1)
            nav = nav.WithView(Controls.Button("Next →").WithAppearance(Appearance.Outline)
                .WithClickAction(c => { c.Host.UpdateData(BlockIndexId, (index + 1).ToString()); return Task.CompletedTask; }));
        nav = nav.WithView(Controls.Button("Open original").WithAppearance(Appearance.Accent)
            .WithClickAction(c => { c.NavigateTo(BuildAreaHref(nodePath, SourceArea, index, terms)); return Task.CompletedTask; }));
        return panel.WithView(nav);
    }

    // ── Source: the original PDF/DOCX viewer with the passage highlighted ──

    /// <summary>
    /// Renders the original source file (PDF/DOCX) for a <c>Document</c> node with the matched passage
    /// highlighted. Reads <c>?q=</c> (the passage/terms) and <c>?index=</c> (so "back to blocks" returns
    /// to the same block). The raw file is served from the <c>/static/…</c> route.
    /// </summary>
    public static IObservable<UiControl?> Source(LayoutAreaHost host, RenderingContext _)
    {
        var terms = host.GetQueryStringParamValue("q") ?? "";
        var index = ParseIndex(host.GetQueryStringParamValue("index"));
        return host.Workspace.GetMeshNodeStream()
            .Select(node => BuildSource(host, node, index, terms))
            .Switch();
    }

    private static IObservable<UiControl?> BuildSource(LayoutAreaHost host, MeshNode? node, int index, string terms)
    {
        var container = Controls.Stack.WithWidth("100%").WithStyle(ContainerStyle);

        var document = node?.ContentAs<Document>(host.Hub.JsonSerializerOptions);
        if (document is null)
            return Observable.Return((UiControl?)container.WithView(Controls.Markdown("_No document data._")));

        var nodePath = node?.Path ?? host.Hub.Address.ToString();
        var fileName = string.IsNullOrEmpty(document.Name) ? document.FilePath : document.Name;
        var fileUrl = BuildStaticUrl(document.CollectionPath, document.FilePath);

        container = container.WithView(Controls.H2(fileName));
        container = container.WithView(Controls.Button("← Back to blocks").WithAppearance(Appearance.Outline)
            .WithClickAction(c => { c.NavigateTo(BuildAreaHref(nodePath, BlocksArea, index, terms)); return Task.CompletedTask; }));

        // Load the deep-linked chunk's stored provenance (page + on-page box) so the viewer marks the exact
        // region — precise and independent of whether the chunk text can be re-found by string match. When
        // the store/chunk/position is absent the viewer falls back to the verbatim text highlight (terms).
        var store = host.Hub.ServiceProvider.GetService<IChunkedContentVectorStore>();
        var provenance = store is null
            ? Observable.Return<ContentChunk?>(null)
            : store.GetChunk(document.CollectionPath, document.FilePath, index)
                .Take(1)
                .Catch<ContentChunk?, Exception>(_ => Observable.Return<ContentChunk?>(null));

        return provenance.Select(chunk => (UiControl?)container.WithView(
            new DocumentSourceControl(fileUrl, document.Mime, terms, fileName)
                .WithMark(chunk?.Page, ChunkPositionJson.Serialize(chunk?.Position))
                .WithStyle("display:block; width:100%; min-height:600px;")));
    }

    /// <summary>
    /// The <c>/static/…</c> URL that serves the raw original file for a collection-relative path. Each
    /// path segment is URL-encoded (so spaces, <c>#</c>, <c>?</c>, unicode in a file name don't break or
    /// truncate the URL) while the <c>/</c> separators are preserved.
    /// </summary>
    private static string BuildStaticUrl(string collectionPath, string filePath)
    {
        var collection = EncodeSegments(collectionPath);
        var file = EncodeSegments(filePath.Replace('\\', '/'));
        return string.IsNullOrEmpty(collection) ? $"/static/{file}" : $"/static/{collection}/{file}";
    }

    /// <summary>URL-encodes each <c>/</c>-delimited segment of <paramref name="path"/>, preserving the slashes.</summary>
    private static string EncodeSegments(string path) =>
        string.Join('/', path.Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString));

    /// <summary>Builds <c>/{nodePath}/{area}?index=N[&amp;q=terms]</c> for navigating between Blocks and Source.</summary>
    private static string BuildAreaHref(string nodePath, string area, int index, string terms)
    {
        var href = $"/{nodePath}/{area}?index={index}";
        if (!string.IsNullOrWhiteSpace(terms))
            href += $"&q={Uri.EscapeDataString(terms)}";
        return href;
    }

    private static int ParseIndex(string? value) =>
        int.TryParse(value, out var i) && i >= 0 ? i : 0;

    private static string EscapeHtml(string? text) => System.Net.WebUtility.HtmlEncode(text ?? "");

    private static string Encode(string? url) => System.Web.HttpUtility.HtmlAttributeEncode(url ?? "");
}
