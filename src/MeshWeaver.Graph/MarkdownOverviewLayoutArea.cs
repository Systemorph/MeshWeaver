using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Domain;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;

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

        // Initialize edit state once per observable subscription (not static)
        var editStateId = $"editState_markdown_{hubPath.Replace("/", "_")}";
        var initialized = new[] { false };

        return nodeStream.Select(nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            return BuildOverview(host, node, editStateId, initialized);
        });
    }

    private static UiControl BuildOverview(LayoutAreaHost host, MeshNode? node,
        string editStateId, bool[] initialized)
    {
        var nodePath = node?.Path ?? host.Hub.Address.ToString();
        var rawContent = GetMarkdownContent(node);
        var canEdit = true; // Permission enforcement happens at save time
        var hasContent = !string.IsNullOrWhiteSpace(rawContent);

        var container = Controls.Stack.WithWidth("100%").WithStyle(MeshNodeLayoutAreas.GetContainerStyle(host));

        // Standard header with title/icon
        container = container.WithView(MeshNodeLayoutAreas.BuildHeader(host, node));

        // Toggleable markdown content: click to edit, Done button to return to read-only
        // Empty content starts in edit mode immediately; only initialize once to prevent
        // nodeStream re-emissions from resetting the edit state while user is typing.
        if (!initialized[0])
        {
            host.UpdateData(editStateId, !hasContent);
            initialized[0] = true;
        }

        container = container.WithView((h, _) =>
            h.Stream.GetDataStream<bool>(editStateId)
                .DistinctUntilChanged()
                .Select(isEditing => isEditing && canEdit
                    ? BuildEditorView(host, nodePath, rawContent, editStateId)
                    : BuildReadOnlyView(host, nodePath, rawContent, canEdit, editStateId)));

        // Standard children section
        container = container.WithView(LayoutAreaControl.Children(host.Hub));

        // Standard inline comments section (if comments enabled)
        if (host.Hub.Configuration.HasComments())
        {
            container = container.WithView(CommentsView.BuildInlineCommentsSection(host));
        }

        return container;
    }

    private static UiControl BuildReadOnlyView(
        LayoutAreaHost host, string nodePath, string rawContent,
        bool canEdit, string editStateId)
    {
        var hasAnnotations = !string.IsNullOrWhiteSpace(rawContent)
            && AnnotationMarkdownExtension.HasAnnotations(rawContent);

        var view = Controls.Stack
            .WithWidth("100%")
            .WithStyle(canEdit && !hasAnnotations ? "cursor: pointer;" : "");

        if (!string.IsNullOrWhiteSpace(rawContent))
        {
            view = view.WithView(
                new CollaborativeMarkdownControl()
                    .WithValue(rawContent)
                    .WithNodePath(nodePath)
                    .WithHubAddress(host.Hub.Address.ToString()));
        }
        else
        {
            view = view.WithView(
                Controls.Html("<p style=\"color: var(--neutral-foreground-hint); font-style: italic;\">No content yet. Click to start writing.</p>"));
        }

        if (canEdit)
        {
            if (hasAnnotations)
            {
                // When annotations are present, don't use click-to-edit on the whole area.
                // Show an explicit Edit button instead to avoid conflicting with annotation clicks.
                view = view.WithView(Controls.Stack
                    .WithOrientation(Orientation.Horizontal)
                    .WithStyle("justify-content: flex-end; margin-top: 8px;")
                    .WithView(Controls.Button("Edit")
                        .WithAppearance(Appearance.Stealth)
                        .WithClickAction(ctx =>
                        {
                            ctx.Host.UpdateData(editStateId, true);
                            return Task.CompletedTask;
                        })));
            }
            else
            {
                view = view.WithClickAction(ctx =>
                {
                    ctx.Host.UpdateData(editStateId, true);
                    return Task.CompletedTask;
                });
            }
        }

        return view;
    }

    private static UiControl BuildEditorView(
        LayoutAreaHost host, string nodePath, string rawContent, string editStateId)
    {
        var stack = Controls.Stack.WithWidth("100%");

        // Done button to switch back to read-only
        stack = stack.WithView(Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("justify-content: flex-end; margin-bottom: 8px;")
            .WithView(Controls.Html("<span style=\"color: var(--neutral-foreground-hint); font-size: 0.85rem; align-self: center; margin-right: 8px;\">Changes are saved automatically</span>"))
            .WithView(Controls.Button("Done")
                .WithAppearance(Appearance.Accent)
                .WithClickAction(ctx =>
                {
                    ctx.Host.UpdateData(editStateId, false);
                    return Task.CompletedTask;
                })));

        // Editor with auto-save
        var editor = new MarkdownEditorControl()
            .WithDocumentId(nodePath)
            .WithValue(rawContent ?? "")
            .WithHeight("400px")
            .WithTrackChanges(true)
            .WithPlaceholder("Start writing your markdown content...")
            .WithAutoSave(host.Hub.Address.ToString(), nodePath);
        stack = stack.WithView(editor);

        return stack;
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
