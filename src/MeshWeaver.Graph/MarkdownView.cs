using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Provides dedicated views for Markdown nodes with a clean, document-focused layout.
/// Features:
/// - Readonly markdown content display by default
/// - Menu button with options for Edit, Comments, Attachments, Settings
/// - Clean typography and reading experience
/// </summary>
public static class MarkdownView
{
    public const string ReadArea = "Read";
    public const string EditArea = "Edit";
    public const string CommentsArea = "Comments";
    public const string AttachmentsArea = "Attachments";

    /// <summary>
    /// Adds the markdown-specific views to the hub's layout.
    /// Sets Read as the default area for a clean reading experience.
    /// </summary>
    public static MessageHubConfiguration AddMarkdownViews(this MessageHubConfiguration configuration)
        => configuration
            .AddLayout(layout => layout
                .WithDefaultArea(ReadArea)
                .WithView(ReadArea, ReadView)
                .WithView(EditArea, EditView)
                .WithView(CommentsArea, CommentsView)
                .WithView(AttachmentsArea, AttachmentsView)
                .WithView(MeshNodeView.SettingsArea, MeshNodeView.Settings)
                .WithView(MeshNodeView.MetadataArea, MeshNodeView.Metadata)
                .WithView(MeshNodeView.ThumbnailArea, Thumbnail));

    /// <summary>
    /// Renders the readonly markdown view with a clean reading experience.
    /// Includes a header with title and action menu.
    /// </summary>
    public static IObservable<UiControl?> ReadView(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        var nodeStream = host.Workspace.GetStream<MeshNode>()
            ?? Observable.Return<IReadOnlyCollection<MeshNode>>(Array.Empty<MeshNode>());

        return nodeStream.Select(nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            return BuildReadView(host, node);
        }).StartWith(Controls.Markdown($"*Loading...*"));
    }

    private static UiControl BuildReadView(LayoutAreaHost host, MeshNode? node)
    {
        var nodePath = node?.Path ?? host.Hub.Address.ToString();

        // Main container with max-width for optimal reading
        var container = Controls.Stack
            .WithWidth("100%")
            .WithStyle("max-width: 900px; margin: 0 auto; padding: 24px;");

        // Header: Title on left, menu button on right
        var title = node?.Name ?? "Document";
        var headerStack = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithWidth("100%")
            .WithStyle("align-items: center; justify-content: space-between; margin-bottom: 24px; padding-bottom: 16px; border-bottom: 1px solid var(--neutral-stroke-rest);");

        // Title with optional icon
        var titleSection = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("align-items: center; gap: 12px;");

        if (!string.IsNullOrEmpty(node?.IconName))
        {
            titleSection = titleSection.WithView(
                Controls.Html($"<fluent-icon name=\"{node.IconName}\" style=\"font-size: 28px; color: var(--accent-fill-rest);\"></fluent-icon>"));
        }

        titleSection = titleSection.WithView(
            Controls.Html($"<h1 style=\"margin: 0; font-size: 1.75rem; font-weight: 600;\">{title}</h1>"));

        headerStack = headerStack.WithView(titleSection);

        // Action menu button
        headerStack = headerStack.WithView(BuildActionMenu(host, node));

        container = container.WithView(headerStack);

        // Main content area - markdown rendered in a clean reading style
        var content = GetMarkdownContent(node);
        if (!string.IsNullOrWhiteSpace(content))
        {
            var markdownContainer = Controls.Stack
                .WithWidth("100%")
                .WithStyle("line-height: 1.7; font-size: 1rem;")
                .WithView(new MarkdownControl(content));

            container = container.WithView(markdownContainer);
        }
        else
        {
            container = container.WithView(
                Controls.Html("<p style=\"color: var(--neutral-foreground-hint); font-style: italic;\">No content yet. Click Edit to add content.</p>"));
        }

        return container;
    }

    private static UiControl BuildActionMenu(LayoutAreaHost host, MeshNode? node)
    {
        var nodePath = node?.Path ?? host.Hub.Address.ToString();

        // Create menu items
        var menuItems = Controls.Stack
            .WithOrientation(Orientation.Vertical)
            .WithStyle("min-width: 180px;");

        // Edit option
        var editHref = $"/{nodePath}/{EditArea}";
        menuItems = menuItems.WithView(
            Controls.MenuItem("Edit", FluentIcons.Edit(IconSize.Size16))
                .WithStyle("width: 100%;")
                .WithClickAction(ctx => ctx.Host.UpdateArea(ctx.Area, new RedirectControl(editHref))));

        // Comments option
        var commentsHref = $"/{nodePath}/{CommentsArea}";
        menuItems = menuItems.WithView(
            Controls.MenuItem("Comments", FluentIcons.Comment(IconSize.Size16))
                .WithStyle("width: 100%;")
                .WithClickAction(ctx => ctx.Host.UpdateArea(ctx.Area, new RedirectControl(commentsHref))));

        // Attachments option
        var attachmentsHref = $"/{nodePath}/{AttachmentsArea}";
        menuItems = menuItems.WithView(
            Controls.MenuItem("Attachments", FluentIcons.Attach(IconSize.Size16))
                .WithStyle("width: 100%;")
                .WithClickAction(ctx => ctx.Host.UpdateArea(ctx.Area, new RedirectControl(attachmentsHref))));

        // Divider
        menuItems = menuItems.WithView(
            Controls.Html("<hr style=\"margin: 8px 0; border: none; border-top: 1px solid var(--neutral-stroke-rest);\" />"));

        // Settings option
        var settingsHref = $"/{nodePath}/{MeshNodeView.SettingsArea}";
        menuItems = menuItems.WithView(
            Controls.MenuItem("Settings", FluentIcons.Settings(IconSize.Size16))
                .WithStyle("width: 100%;")
                .WithClickAction(ctx => ctx.Host.UpdateArea(ctx.Area, new RedirectControl(settingsHref))));

        // Metadata option
        var metadataHref = $"/{nodePath}/{MeshNodeView.MetadataArea}";
        menuItems = menuItems.WithView(
            Controls.MenuItem("Properties", FluentIcons.Info(IconSize.Size16))
                .WithStyle("width: 100%;")
                .WithClickAction(ctx => ctx.Host.UpdateArea(ctx.Area, new RedirectControl(metadataHref))));

        // Create the menu button with icon
        return Controls.MenuItem("", FluentIcons.MoreHorizontal(IconSize.Size20))
            .WithAppearance(Appearance.Stealth)
            .WithStyle("border-radius: 4px;")
            .WithView(menuItems);
    }

    /// <summary>
    /// Renders the edit view for markdown content.
    /// Uses Monaco editor with collaborative editing support.
    /// </summary>
    public static IObservable<UiControl?> EditView(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        var nodeStream = host.Workspace.GetStream<MeshNode>()
            ?? Observable.Return<IReadOnlyCollection<MeshNode>>(Array.Empty<MeshNode>());

        return nodeStream.Select(nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            return BuildEditView(host, node);
        }).StartWith(Controls.Markdown($"*Loading editor...*"));
    }

    private static UiControl BuildEditView(LayoutAreaHost host, MeshNode? node)
    {
        var nodePath = node?.Path ?? host.Hub.Address.ToString();
        var title = node?.Name ?? "Edit Document";

        var container = Controls.Stack
            .WithWidth("100%")
            .WithStyle("height: 100%; display: flex; flex-direction: column;");

        // Header with back button and title
        var headerStack = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithWidth("100%")
            .WithStyle("align-items: center; gap: 16px; padding: 16px 24px; border-bottom: 1px solid var(--neutral-stroke-rest); flex-shrink: 0;");

        // Back button
        var readHref = $"/{nodePath}/{ReadArea}";
        headerStack = headerStack.WithView(
            Controls.Button("")
                .WithIconStart(FluentIcons.ArrowLeft(IconSize.Size16))
                .WithAppearance(Appearance.Stealth)
                .WithClickAction(ctx => ctx.Host.UpdateArea(ctx.Area, new RedirectControl(readHref))));

        headerStack = headerStack.WithView(
            Controls.Html($"<h2 style=\"margin: 0; font-size: 1.25rem;\">Editing: {title}</h2>"));

        container = container.WithView(headerStack);

        // Editor placeholder (TODO: integrate with Monaco/collaborative editing)
        var content = GetMarkdownContent(node);
        var editorArea = Controls.Stack
            .WithWidth("100%")
            .WithStyle("flex: 1; padding: 24px; overflow: auto;")
            .WithView(Controls.Html($@"
                <div style=""background: var(--neutral-layer-2); border-radius: 8px; padding: 16px; height: 100%; min-height: 400px;"">
                    <p style=""color: var(--neutral-foreground-hint); margin-bottom: 16px;"">
                        <strong>Editor Mode</strong> - Edit your markdown content below
                    </p>
                    <textarea style=""width: 100%; height: calc(100% - 60px); min-height: 300px;
                        font-family: 'Cascadia Code', 'Fira Code', monospace; font-size: 14px;
                        padding: 16px; border: 1px solid var(--neutral-stroke-rest);
                        border-radius: 4px; background: var(--neutral-layer-1);
                        color: var(--neutral-foreground-rest); resize: vertical;""
                        placeholder=""Start writing your markdown content..."">{System.Web.HttpUtility.HtmlEncode(content)}</textarea>
                </div>
            "));

        container = container.WithView(editorArea);

        return container;
    }

    /// <summary>
    /// Renders the comments view showing all comments on the document.
    /// </summary>
    public static IObservable<UiControl?> CommentsView(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        var nodeStream = host.Workspace.GetStream<MeshNode>()
            ?? Observable.Return<IReadOnlyCollection<MeshNode>>(Array.Empty<MeshNode>());

        return nodeStream.Select(nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            return BuildCommentsView(host, node);
        }).StartWith(Controls.Markdown($"*Loading comments...*"));
    }

    private static UiControl BuildCommentsView(LayoutAreaHost host, MeshNode? node)
    {
        var nodePath = node?.Path ?? host.Hub.Address.ToString();
        var title = node?.Name ?? "Document";

        var container = Controls.Stack
            .WithWidth("100%")
            .WithStyle("max-width: 900px; margin: 0 auto; padding: 24px;");

        // Header with back button
        var headerStack = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithWidth("100%")
            .WithStyle("align-items: center; gap: 16px; margin-bottom: 24px; padding-bottom: 16px; border-bottom: 1px solid var(--neutral-stroke-rest);");

        var readHref = $"/{nodePath}/{ReadArea}";
        headerStack = headerStack.WithView(
            Controls.Button("")
                .WithIconStart(FluentIcons.ArrowLeft(IconSize.Size16))
                .WithAppearance(Appearance.Stealth)
                .WithClickAction(ctx => ctx.Host.UpdateArea(ctx.Area, new RedirectControl(readHref))));

        headerStack = headerStack.WithView(
            Controls.Html($"<h2 style=\"margin: 0;\">Comments on: {title}</h2>"));

        container = container.WithView(headerStack);

        // Comments list placeholder
        container = container.WithView(
            Controls.Html(@"
                <div style=""background: var(--neutral-layer-2); border-radius: 8px; padding: 32px; text-align: center;"">
                    <fluent-icon name=""Comment"" style=""font-size: 48px; color: var(--neutral-foreground-hint); margin-bottom: 16px;""></fluent-icon>
                    <p style=""color: var(--neutral-foreground-hint); margin: 0;"">No comments yet</p>
                    <p style=""color: var(--neutral-foreground-hint); font-size: 0.9rem; margin-top: 8px;"">
                        Comments made using track changes will appear here
                    </p>
                </div>
            "));

        return container;
    }

    /// <summary>
    /// Renders the attachments view showing files attached to the document.
    /// </summary>
    public static IObservable<UiControl?> AttachmentsView(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        var nodeStream = host.Workspace.GetStream<MeshNode>()
            ?? Observable.Return<IReadOnlyCollection<MeshNode>>(Array.Empty<MeshNode>());

        return nodeStream.Select(nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            return BuildAttachmentsView(host, node);
        }).StartWith(Controls.Markdown($"*Loading attachments...*"));
    }

    private static UiControl BuildAttachmentsView(LayoutAreaHost host, MeshNode? node)
    {
        var nodePath = node?.Path ?? host.Hub.Address.ToString();
        var title = node?.Name ?? "Document";

        var container = Controls.Stack
            .WithWidth("100%")
            .WithStyle("max-width: 900px; margin: 0 auto; padding: 24px;");

        // Header with back button
        var headerStack = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithWidth("100%")
            .WithStyle("align-items: center; gap: 16px; margin-bottom: 24px; padding-bottom: 16px; border-bottom: 1px solid var(--neutral-stroke-rest);");

        var readHref = $"/{nodePath}/{ReadArea}";
        headerStack = headerStack.WithView(
            Controls.Button("")
                .WithIconStart(FluentIcons.ArrowLeft(IconSize.Size16))
                .WithAppearance(Appearance.Stealth)
                .WithClickAction(ctx => ctx.Host.UpdateArea(ctx.Area, new RedirectControl(readHref))));

        headerStack = headerStack.WithView(
            Controls.Html($"<h2 style=\"margin: 0;\">Attachments: {title}</h2>"));

        container = container.WithView(headerStack);

        // Attachments list placeholder
        container = container.WithView(
            Controls.Html(@"
                <div style=""background: var(--neutral-layer-2); border-radius: 8px; padding: 32px; text-align: center;"">
                    <fluent-icon name=""Attach"" style=""font-size: 48px; color: var(--neutral-foreground-hint); margin-bottom: 16px;""></fluent-icon>
                    <p style=""color: var(--neutral-foreground-hint); margin: 0;"">No attachments</p>
                    <p style=""color: var(--neutral-foreground-hint); font-size: 0.9rem; margin-top: 8px;"">
                        Drag and drop files here to attach them
                    </p>
                </div>
            "));

        return container;
    }

    /// <summary>
    /// Renders a compact thumbnail for markdown nodes in catalogs.
    /// </summary>
    public static UiControl Thumbnail(LayoutAreaHost host, RenderingContext ctx)
    {
        var hubPath = host.Hub.Address.ToString();

        var nodeStream = host.Workspace.GetStream<MeshNode>()
            ?? Observable.Return<IReadOnlyCollection<MeshNode>>(Array.Empty<MeshNode>());

        return Controls.Stack
            .WithView((h, c) => nodeStream.Select(nodes =>
            {
                var node = nodes.FirstOrDefault(n => n.Path == hubPath);
                return BuildThumbnail(node, hubPath);
            }));
    }

    private static UiControl BuildThumbnail(MeshNode? node, string hubPath)
    {
        var title = node?.Name ?? hubPath.Split('/').LastOrDefault() ?? "Document";
        var description = node?.Description ?? "";
        var iconName = node?.IconName ?? "Document";

        // Truncate description for thumbnail
        if (description.Length > 100)
            description = description[..97] + "...";

        var href = $"/{hubPath}";

        return Controls.Html($@"
            <a href=""{href}"" style=""text-decoration: none; color: inherit; display: block;"">
                <div style=""background: var(--neutral-layer-2); border-radius: 8px; padding: 16px;
                    border: 1px solid var(--neutral-stroke-rest); transition: all 0.2s ease;
                    cursor: pointer;""
                    onmouseover=""this.style.borderColor='var(--accent-fill-rest)'; this.style.transform='translateY(-2px)';""
                    onmouseout=""this.style.borderColor='var(--neutral-stroke-rest)'; this.style.transform='none';"">
                    <div style=""display: flex; align-items: center; gap: 12px; margin-bottom: 8px;"">
                        <fluent-icon name=""{iconName}"" style=""font-size: 24px; color: var(--accent-fill-rest);""></fluent-icon>
                        <span style=""font-weight: 600; font-size: 1rem;"">{title}</span>
                    </div>
                    {(string.IsNullOrEmpty(description) ? "" : $"<p style=\"margin: 0; color: var(--neutral-foreground-hint); font-size: 0.875rem; line-height: 1.4;\">{description}</p>")}
                </div>
            </a>
        ");
    }

    /// <summary>
    /// Extracts markdown content from a MeshNode.
    /// Handles MarkdownDocument JSON content format.
    /// </summary>
    private static string GetMarkdownContent(MeshNode? node)
    {
        if (node?.Content == null)
            return string.Empty;

        // Handle MarkdownDocument content (JSON with $type and content fields)
        if (node.Content is System.Text.Json.JsonElement jsonElement)
        {
            if (jsonElement.TryGetProperty("$type", out var typeProperty))
            {
                var typeName = typeProperty.GetString();
                if (typeName == "MarkdownDocument" && jsonElement.TryGetProperty("content", out var contentProperty))
                {
                    return contentProperty.GetString() ?? string.Empty;
                }
            }
        }

        // Fall back to Description
        return node.Description ?? string.Empty;
    }
}
