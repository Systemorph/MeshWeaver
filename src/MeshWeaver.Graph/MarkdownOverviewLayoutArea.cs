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
    public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return(Array.Empty<MeshNode>());

        return nodeStream.SelectMany(async nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            var canComment = await PermissionHelper.CanCommentAsync(host.Hub, hubPath);
            var canEdit = await PermissionHelper.CanEditAsync(host.Hub, hubPath);
            return (UiControl?)BuildOverview(host, node, canComment, canEdit);
        });
    }

    private static UiControl BuildOverview(LayoutAreaHost host, MeshNode? node, bool canComment, bool canEdit)
    {
        var nodePath = node?.Path ?? host.Hub.Address.ToString();
        var rawContent = GetMarkdownContent(node);

        var container = Controls.Stack.WithWidth("100%").WithStyle(MeshNodeLayoutAreas.GetContainerStyle(host));

        // Standard header with title/icon
        container = container.WithView(MeshNodeLayoutAreas.BuildHeader(host, node, false));

        // Read-only markdown content
        container = container.WithView(BuildReadOnlyView(host, nodePath, rawContent, canComment, canEdit));

        // Standard children section
        container = container.WithView(LayoutAreaControl.Children(host.Hub).WithShowProgress(false));

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

    private static UiControl BuildReadOnlyView(
        LayoutAreaHost host, string nodePath, string rawContent, bool canComment, bool canEdit)
    {
        var view = Controls.Stack.WithWidth("100%");

        if (!string.IsNullOrWhiteSpace(rawContent))
        {
            view = view.WithView(
                new CollaborativeMarkdownControl()
                    .WithValue(rawContent)
                    .WithNodePath(nodePath)
                    .WithHubAddress(host.Hub.Address.ToString())
                    .WithCanComment(canComment)
                    .WithCanEdit(canEdit));
        }
        else
        {
            view = view.WithView(
                Controls.Html("<p style=\"color: var(--neutral-foreground-hint); font-style: italic;\">No content yet. Use the menu to start editing.</p>"));
        }

        return view;
    }

    public static UiControl Thumbnail(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return(Array.Empty<MeshNode>());

        return Controls.Stack
            .WithView((h, c) => nodeStream.Select(nodes =>
            {
                var node = nodes.FirstOrDefault(n => n.Path == hubPath);
                return MeshNodeThumbnailControl.FromNode(node, hubPath);
            }));
    }

    /// <summary>
    /// Extracts markdown content from a MeshNode.
    /// </summary>
    internal static string GetMarkdownContent(MeshNode? node)
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
                if (typeName == "MarkdownDocument" && jsonElement.TryGetProperty("content", out var contentProperty))
                {
                    return contentProperty.GetString() ?? string.Empty;
                }
            }
        }

        return string.Empty;
    }
}
