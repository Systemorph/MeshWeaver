using System.Reactive.Linq;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;

namespace MeshWeaver.Graph;

/// <summary>
/// Overview (read-only) layout area for Markdown nodes.
/// Renders a CollaborativeMarkdownControl for the content with annotation support.
/// Uses the standard MeshNodeLayoutAreas action menu and children patterns.
/// </summary>
public static class MarkdownOverviewLayoutArea
{
    /// <summary>
    /// Renders the read-only Overview layout area for a Markdown node, including the
    /// collaborative markdown body, children, approvals, and inline comments.
    /// </summary>
    /// <param name="host">The layout area host rendering the area.</param>
    /// <param name="_">The rendering context for the area.</param>
    /// <returns>An observable stream of the view for the Overview layout area.</returns>
    public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var permissionsStream = host.Hub.GetEffectivePermissions(hubPath);

        return host.Workspace.GetMeshNodeStream()
            .CombineLatest(permissionsStream, (node, perms) =>
            {
                var canComment = perms.HasFlag(Permission.Comment) || perms.HasFlag(Permission.Update);
                var canEdit = perms.HasFlag(Permission.Update);
                return (UiControl?)BuildOverview(host, node, canComment, canEdit);
            });
    }

    private static UiControl BuildOverview(LayoutAreaHost host, MeshNode? node, bool canComment, bool canEdit)
    {
        var nodePath = node?.Path ?? host.Hub.Address.ToString();
        var rawContent = GetMarkdownContent(node);

        // Markdown pages render full width (max-width: 100%), not the centered 1200px reading column.
        var container = Controls.Stack.WithWidth("100%").WithStyle(MeshNodeLayoutAreas.GetContainerStyle(host, maxWidthOverride: "100%"));

        // Standard header with title/icon
        container = container.WithView(MeshNodeLayoutAreas.BuildHeader(host, node, false));

        // Read-only markdown content — the CollaborativeMarkdownControl is added as
        // a DIRECT child of `container` so agents and tests can locate it without
        // walking through an intermediate Stack wrapper.
        container = container.WithView(BuildMarkdownReadView(host, nodePath, rawContent, canComment, canEdit));

        // Standard children section — separated from main content
        container = container.WithView(
            Controls.Stack
                .WithWidth("100%")
                .WithStyle("margin-top: 48px; padding-top: 24px; border-top: 1px solid var(--neutral-stroke-rest);")
                .WithView(LayoutAreaControl.Children(host.Hub).WithShowProgress(false)));

        // Approvals section (only if enabled and approvals exist)
        if (host.Hub.Configuration.HasApprovals())
            container = container.WithView(Controls.LayoutArea(host.Hub.Address, "Approvals").WithShowProgress(false));

        // Standard inline comments section (if comments enabled)
        if (host.Hub.Configuration.HasComments())
        {
            container = container.WithView(CommentsView.BuildInlineCommentsSection(host));
        }

        return container;
    }

    /// <summary>
    /// Returns the actual markdown body control (a <see cref="CollaborativeMarkdownControl"/>
    /// when there is content, an HTML placeholder when empty) — NOT wrapped in an extra
    /// Stack. The caller is expected to add this directly to its container so consumers
    /// can identify the markdown body via <c>OfType&lt;CollaborativeMarkdownControl&gt;</c>
    /// without skipping a wrapper layer.
    /// </summary>
    private static UiControl BuildMarkdownReadView(
        LayoutAreaHost host, string nodePath, string rawContent, bool canComment, bool canEdit)
    {
        if (!string.IsNullOrWhiteSpace(rawContent))
        {
            return new CollaborativeMarkdownControl()
                .WithValue(rawContent)
                .WithNodePath(nodePath)
                .WithHubAddress(host.Hub.Address.ToString())
                .WithCanComment(canComment)
                .WithCanEdit(canEdit);
        }
        return Controls.Html("<p style=\"color: var(--neutral-foreground-hint); font-style: italic;\">No content yet. Use the menu to start editing.</p>");
    }

    /// <summary>
    /// Renders the Thumbnail layout area — a compact card representation of the Markdown node.
    /// </summary>
    /// <param name="host">The layout area host rendering the area.</param>
    /// <param name="_">The rendering context for the area.</param>
    /// <returns>The view for the Thumbnail layout area.</returns>
    public static UiControl Thumbnail(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        return Controls.Stack
            .WithView((h, c) => host.Workspace.GetMeshNodeStream()
                .Select(node => MeshNodeThumbnailControl.FromNode(node, hubPath)));
    }

    /// <summary>
    /// Extracts markdown content from a MeshNode.
    /// </summary>
    public static string GetMarkdownContent(MeshNode? node)
    {
        if (node?.Content == null)
            return string.Empty;

        if (node.Content is MarkdownContent markdownContent)
            return markdownContent.Content;

        if (node.Content is string stringContent)
            return stringContent;

        if (node.Content is System.Text.Json.JsonElement jsonElement)
        {
            if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.String)
                return jsonElement.GetString() ?? string.Empty;

            if (jsonElement.TryGetProperty("$type", out var typeProperty))
            {
                var typeName = typeProperty.GetString();
                if ((typeName == "MarkdownDocument" || typeName == "MarkdownContent") && jsonElement.TryGetProperty("content", out var contentProperty))
                {
                    return contentProperty.GetString() ?? string.Empty;
                }
            }

            // Fallback: try "content" property without $type check
            if (jsonElement.TryGetProperty("content", out var fallbackContent) && fallbackContent.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return fallbackContent.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }
}
