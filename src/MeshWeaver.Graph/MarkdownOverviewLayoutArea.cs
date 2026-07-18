using System.Collections.Generic;
using System.Linq;
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

        // When rendered as an @@ embed, the inline reference carries ?hideHeader=true
        // (set by LayoutAreaMarkdownRenderer) — suppress the header, comments and side menu.
        var hideHeader = host.Reference.HasParameter("hideHeader")
            && !string.Equals(host.Reference.GetParameterValue("hideHeader"), "false", System.StringComparison.OrdinalIgnoreCase);

        return host.Workspace.GetMeshNodeStream()
            .CombineLatest(permissionsStream, host.ObserveChildren("is:main"), (node, perms, children) =>
            {
                var canComment = perms.HasFlag(Permission.Comment) || perms.HasFlag(Permission.Update);
                var canEdit = perms.HasFlag(Permission.Update);
                var content = (UiControl)BuildOverview(host, node, canComment, canEdit, hideHeader);

                // A markdown node with sub-nodes gets a collapsible side menu of them. Skipped for
                // @@ embeds and when there are none; internal satellites (_Access, _Thread, …) excluded.
                if (hideHeader)
                    return (UiControl?)content;
                var subNodes = children
                    .Where(c => !LastSegment(c.Path).StartsWith('_'))
                    .OrderBy(c => c.Name ?? c.Id, System.StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return (UiControl?)(subNodes.Count == 0 ? content : BuildWithSubNodeNav(node, subNodes, content));
            });
    }

    private static string LastSegment(string path)
    {
        var i = path.LastIndexOf('/');
        return i < 0 ? path : path[(i + 1)..];
    }

    /// <summary>
    /// Wraps the page content with a collapsible left-hand NavMenu listing the node's sub-nodes,
    /// giving every markdown node with children a navigable side menu of them.
    /// </summary>
    private static UiControl BuildWithSubNodeNav(MeshNode? node, IReadOnlyList<MeshNode> subNodes, UiControl content)
    {
        var group = new NavGroupControl(node?.Name ?? "Contents").WithSkin(s => s.WithExpanded(true));
        foreach (var child in subNodes)
        {
            var href = $"/{child.Path}";
            var icon = MeshNodeImageHelper.ResolveNodeIcon(child);
            group = icon is null
                ? group.WithView(new NavLinkControl(child.Name ?? child.Id, null, href))
                : group.WithView(new NavLinkControl(child.Name ?? child.Id, icon, href));
        }

        var nav = Controls.NavMenu.WithSkin(s => s.WithWidth(240).WithCollapsible(true)).WithNavGroup(group);

        return Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithWidth("100%")
            .WithStyle("gap: 24px; align-items: flex-start;")
            .WithView(nav)
            .WithView(Controls.Stack.WithStyle("flex: 1; min-width: 0;").WithView(content));
    }

    private static UiControl BuildOverview(LayoutAreaHost host, MeshNode? node, bool canComment, bool canEdit, bool hideHeader)
    {
        var nodePath = node?.Path ?? host.Hub.Address.ToString();
        var rawContent = GetMarkdownContent(node);

        // Markdown pages render full width (max-width: 100%), not the centered 1200px reading column.
        var container = Controls.Stack.WithWidth("100%").WithStyle(MeshNodeLayoutAreas.GetContainerStyle(host, maxWidthOverride: "100%"));

        // Standard header with title/icon (skipped for @@ embeds)
        if (!hideHeader)
            container = container.WithView(MeshNodeLayoutAreas.BuildHeader(host, node, false));

        // Read-only markdown content — the CollaborativeMarkdownControl is added as
        // a DIRECT child of `container` so agents and tests can locate it without
        // walking through an intermediate Stack wrapper.
        container = container.WithView(BuildMarkdownReadView(host, nodePath, rawContent, canComment, canEdit));

        // No hardcoded children section: a node page is a markdown space — children (or any other
        // content) are injected INLINE with the @@(query) operator, never auto-listed (that doubled
        // the children on every page that already referenced them).

        // Approvals section (only if enabled and approvals exist)
        if (host.Hub.Configuration.HasApprovals())
            container = container.WithView(Controls.LayoutArea(host.Hub.Address, "Approvals").WithShowProgress(false));

        // Standard inline comments section (if comments enabled)
        if (!hideHeader && host.Hub.Configuration.HasComments())
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
