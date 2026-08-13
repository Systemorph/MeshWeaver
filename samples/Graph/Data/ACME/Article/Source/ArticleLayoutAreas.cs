// <meshweaver>
// Id: ArticleLayoutAreas
// DisplayName: ACME Software Article Views
// </meshweaver>

using System.Reactive.Linq;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;

/// <summary>
/// Views for ACME Software Article nodes.
/// </summary>
public static class ArticleLayoutAreas
{
    /// <summary>
    /// Registers article views with the layout definition.
    /// </summary>
    public static LayoutDefinition AddArticleLayoutAreas(this LayoutDefinition layout) =>
        layout
            .WithView("Overview", Overview)
            .WithView("Thumbnail", Thumbnail);

    /// <summary>
    /// Overview view showing article content with metadata header.
    /// </summary>
    public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return(Array.Empty<MeshNode>());

        return nodeStream.Select(nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            if (node == null)
                return (UiControl?)Controls.Markdown("*Loading article...*");

            return (UiControl?)BuildArticleOverview(host, node);
        });
    }

    private static UiControl BuildArticleOverview(LayoutAreaHost host, MeshNode node)
    {
        var container = Controls.Stack.WithWidth("100%")
            .WithStyle("max-width: 960px; margin: 0 auto; padding: 0 24px;");

        // Title
        container = container.WithView(
            Controls.Html($"<h1 style=\"margin: 0 0 8px 0;\">{System.Web.HttpUtility.HtmlEncode(node.Name ?? "Article")}</h1>"));

        // Metadata bar: authors, published date, tags.
        // ContentAs, never `as`: a node whose content was stored as bare JSON (an import, an MCP
        // create/patch carrying a raw body) has no $type for the polymorphic converter to resolve,
        // so it arrives — and stays, even on this article's OWN hub — a raw JsonElement. `as` is
        // then silently null and the whole header vanishes with no exception and no log line.
        var mdContent = node.ContentAs<MarkdownContent>(host.Hub.JsonSerializerOptions);
        var metaParts = new List<string>();

        if (mdContent?.Authors?.Count > 0)
            metaParts.Add(string.Join(", ", mdContent.Authors));

        if (node.LastModified != default)
            metaParts.Add(node.LastModified.ToString("MMMM d, yyyy"));

        if (metaParts.Count > 0)
        {
            var metaHtml = string.Join(" &middot; ", metaParts);

            // Add tags as styled badges
            if (mdContent?.Tags?.Count > 0)
            {
                var tagBadges = string.Join(" ", mdContent.Tags.Select(t =>
                    $"<span style=\"background: var(--neutral-fill-secondary-rest); padding: 2px 8px; border-radius: 4px; font-size: 0.85em;\">{System.Web.HttpUtility.HtmlEncode(t)}</span>"));
                metaHtml += $" &middot; {tagBadges}";
            }

            container = container.WithView(Controls.Html(
                $"<div style=\"color: var(--neutral-foreground-hint); margin-bottom: 24px; font-size: 0.9em; display: flex; align-items: center; gap: 8px; flex-wrap: wrap;\">{metaHtml}</div>"));
        }

        // Thumbnail image
        if (!string.IsNullOrEmpty(mdContent?.Thumbnail))
        {
            var thumbnail = mdContent.Thumbnail;
            string imgSrc;
            if (thumbnail.StartsWith("/") || thumbnail.StartsWith("http"))
                imgSrc = thumbnail;
            else
            {
                var ns = node.Namespace;
                imgSrc = !string.IsNullOrEmpty(ns)
                    ? $"/api/content/{ns}/{thumbnail}"
                    : thumbnail;
            }
            container = container.WithView(Controls.Html(
                $"<img src=\"{imgSrc}\" alt=\"\" style=\"max-width: 100%; border-radius: 8px; margin-bottom: 24px;\" />"));
        }

        // Markdown body content. MarkdownBody.Of is the framework's single reader — shared with the
        // export templates (ExportSource.MarkdownOf) precisely so this extractor is not hand-copied
        // into every sample space again.
        var rawContent = MarkdownBody.Of(node, host.Hub.JsonSerializerOptions);
        if (!string.IsNullOrEmpty(rawContent))
        {
            container = container.WithView(Controls.Markdown(rawContent));
        }

        // No children section — children are injected inline with the @@(query) operator, or
        // browsed via the Catalog / Search areas (the framework dropped its hardcoded one too).

        return container;
    }

    /// <summary>
    /// Thumbnail view for catalog display.
    /// </summary>
    public static IObservable<UiControl?> Thumbnail(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return(Array.Empty<MeshNode>());

        return nodeStream.Select(nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            return (UiControl?)MeshNodeThumbnailControl.FromNode(node, hubPath);
        });
    }
}
