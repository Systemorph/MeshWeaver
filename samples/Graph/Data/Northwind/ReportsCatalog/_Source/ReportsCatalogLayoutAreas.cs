// <meshweaver>
// Id: ReportsCatalogLayoutAreas
// DisplayName: Reports Catalog Views
// </meshweaver>

using System.Reactive.Linq;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Views for ReportsCatalog nodes — rich overview with report cards.
/// </summary>
public static class ReportsCatalogLayoutAreas
{
    /// <summary>
    /// Registers reports catalog views with the layout definition.
    /// </summary>
    public static LayoutDefinition AddReportsCatalogLayoutAreas(this LayoutDefinition layout) =>
        layout
            .WithView("Overview", Overview)
            .WithView("Thumbnail", Thumbnail);

    /// <summary>
    /// Overview view showing catalog header and rich report cards with abstract + thumbnail.
    /// </summary>
    public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return(Array.Empty<MeshNode>());

        return nodeStream.SelectMany(async nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);

            // Query child report nodes
            var children = await host.QueryChildrenAsync("").ToListAsync();

            return (UiControl?)BuildCatalogOverview(host, node, children);
        });
    }

    private static UiControl BuildCatalogOverview(LayoutAreaHost host, MeshNode? node, IReadOnlyList<MeshNode> children)
    {
        var container = Controls.Stack.WithWidth("100%")
            .WithStyle("max-width: 960px; margin: 0 auto; padding: 0 24px;");

        // Header with title and icon
        var title = node?.Name ?? "Reports";
        var iconValue = node?.Icon;

        var headerStack = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("align-items: center; gap: 16px; margin-bottom: 24px;");

        if (!string.IsNullOrEmpty(iconValue))
        {
            if (iconValue.StartsWith("/") || iconValue.StartsWith("http"))
            {
                headerStack = headerStack.WithView(Controls.Html(
                    $"<img src=\"{System.Web.HttpUtility.HtmlEncode(iconValue)}\" alt=\"\" style=\"width: 40px; height: 40px;\" />"));
            }
            else
            {
                headerStack = headerStack.WithView(Controls.Icon(iconValue)
                    .WithStyle("font-size: 40px;"));
            }
        }

        headerStack = headerStack.WithView(Controls.Html(
            $"<h1 style=\"margin: 0;\">{System.Web.HttpUtility.HtmlEncode(title)}</h1>"));

        container = container.WithView(headerStack);

        // Report cards
        foreach (var child in children)
        {
            container = container.WithView(BuildReportCard(child));
        }

        return container;
    }

    private static UiControl BuildReportCard(MeshNode child)
    {
        var mc = child.Content as MarkdownContent;
        var childTitle = System.Web.HttpUtility.HtmlEncode(child.Name ?? child.Id);
        var abstractText = System.Web.HttpUtility.HtmlEncode(mc?.Abstract ?? "");
        var path = child.Path;

        // Build metadata line
        var metaParts = new List<string>();
        if (mc?.Authors?.Count > 0)
            metaParts.Add(System.Web.HttpUtility.HtmlEncode(string.Join(", ", mc.Authors)));
        if (child.LastModified != default)
            metaParts.Add(child.LastModified.ToString("MMMM d, yyyy"));

        var metaHtml = metaParts.Count > 0
            ? $"<div style=\"font-size: 13px; color: var(--neutral-foreground-hint); margin-top: 8px;\">{string.Join(" &middot; ", metaParts)}</div>"
            : "";

        // Build tags
        var tagsHtml = "";
        if (mc?.Tags?.Count > 0)
        {
            var badges = string.Join("", mc.Tags.Select(t =>
                $"<span style=\"display: inline-block; background: var(--neutral-layer-4, #e8e8e8); color: var(--neutral-foreground-rest); padding: 2px 10px; border-radius: 12px; font-size: 12px; margin-right: 6px; margin-top: 6px;\">{System.Web.HttpUtility.HtmlEncode(t)}</span>"));
            tagsHtml = $"<div style=\"margin-top: 8px;\">{badges}</div>";
        }

        // Left side: title, abstract, metadata, tags
        var leftHtml = $@"<div style=""flex: 1; min-width: 0;"">
  <a href=""/{System.Web.HttpUtility.HtmlEncode(path)}"" style=""text-decoration: none; color: inherit;"">
    <h3 style=""margin: 0 0 8px 0; font-size: 18px;"">{childTitle}</h3>
  </a>
  <p style=""margin: 0; color: var(--neutral-foreground-rest); line-height: 1.5; font-size: 14px;"">{abstractText}</p>
  {metaHtml}
  {tagsHtml}
</div>";

        // Right side: thumbnail
        var rightHtml = "";
        if (!string.IsNullOrEmpty(mc?.Thumbnail))
        {
            var thumbnail = mc.Thumbnail;
            string imgSrc;
            if (thumbnail.StartsWith("/") || thumbnail.StartsWith("http"))
                imgSrc = thumbnail;
            else
            {
                var ns = child.Namespace;
                imgSrc = !string.IsNullOrEmpty(ns)
                    ? $"/static/storage/content/{ns}/{thumbnail}"
                    : thumbnail;
            }
            rightHtml = $@"<div style=""flex-shrink: 0; margin-left: 24px;"">
  <a href=""/{System.Web.HttpUtility.HtmlEncode(path)}"" style=""text-decoration: none;"">
    <img src=""{System.Web.HttpUtility.HtmlEncode(imgSrc)}"" alt="""" style=""width: 200px; height: auto; border-radius: 8px;"" />
  </a>
</div>";
        }

        // Card container
        var cardHtml = $@"<div style=""display: flex; align-items: flex-start; padding: 20px; margin-bottom: 16px; border: 1px solid var(--neutral-stroke-rest, #e0e0e0); border-radius: 12px; background: var(--neutral-layer-card-container, #fff);"">
  {leftHtml}
  {rightHtml}
</div>";

        return Controls.Html(cardHtml);
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
