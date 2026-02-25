// <meshweaver>
// Id: ArticleViews
// DisplayName: Cornerstone Article Views
// </meshweaver>

using System.Reactive.Linq;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;

/// <summary>
/// Views for Cornerstone Article nodes.
/// </summary>
public static class ArticleViews
{
    /// <summary>
    /// Registers article views with the layout definition.
    /// </summary>
    public static LayoutDefinition AddArticleViews(this LayoutDefinition layout) =>
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

        // Metadata bar: authors, published date, tags
        var mdContent = node.Content as MarkdownContent;
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
                    ? $"/static/storage/content/{ns}/{thumbnail}"
                    : thumbnail;
            }
            container = container.WithView(Controls.Html(
                $"<img src=\"{imgSrc}\" alt=\"\" style=\"max-width: 100%; border-radius: 8px; margin-bottom: 24px;\" />"));
        }

        // Markdown body content
        var rawContent = GetMarkdownContent(node);
        if (!string.IsNullOrEmpty(rawContent))
        {
            container = container.WithView(Controls.Markdown(rawContent));
        }

        // Children area
        container = container.WithView(LayoutAreaControl.Children(host.Hub));

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

    /// <summary>
    /// Extracts markdown content from a MeshNode.
    /// </summary>
    private static string GetMarkdownContent(MeshNode? node)
    {
        if (node?.Content == null)
            return string.Empty;

        if (node.Content is MarkdownContent markdownContent)
            return markdownContent.Content;

        if (node.Content is string stringContent)
            return stringContent;

        return string.Empty;
    }
}
