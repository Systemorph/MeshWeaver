using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Catalog;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Provides dedicated views for Comment nodes.
/// Shows author, highlighted text quote, comment text, status, and child replies.
/// </summary>
public static class CommentLayoutAreas
{
    public const string OverviewArea = "Overview";
    public const string EditArea = "Edit";

    /// <summary>
    /// Adds the comment node views to the hub's layout.
    /// </summary>
    public static MessageHubConfiguration AddCommentNodeViews(this MessageHubConfiguration configuration)
        => configuration
            .AddLayout(layout => layout
                .WithDefaultArea(OverviewArea)
                .WithView(OverviewArea, Overview)
                .WithView(EditArea, Edit)
                .WithView(MeshNodeLayoutAreas.ThumbnailArea, Thumbnail));

    private const int InitialReplyCount = 3;
    private const int LoadMoreCount = 10;

    /// <summary>
    /// Renders the Overview area for a Comment node.
    /// Shows author, comment text (as rendered markdown), click-to-edit toggle,
    /// and expandable replies section with load-more.
    /// </summary>
    public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var meshQuery = host.Hub.ServiceProvider.GetService<IMeshQuery>();
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var currentUser = accessService?.Context?.Name ?? "";

        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return<MeshNode[]>(Array.Empty<MeshNode>());

        // Observe child Comment nodes for replies
        var repliesStream = meshQuery != null
            ? meshQuery.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                $"path:{hubPath} nodeType:{CommentNodeType.NodeType} scope:children"))
                .Scan(new List<MeshNode>(), (list, change) =>
                {
                    if (change.ChangeType == QueryChangeType.Initial || change.ChangeType == QueryChangeType.Reset)
                        return change.Items.ToList();
                    foreach (var item in change.Items)
                    {
                        if (change.ChangeType == QueryChangeType.Added)
                            list.Add(item);
                        else if (change.ChangeType == QueryChangeType.Removed)
                            list.RemoveAll(n => n.Path == item.Path);
                        else if (change.ChangeType == QueryChangeType.Updated)
                        {
                            list.RemoveAll(n => n.Path == item.Path);
                            list.Add(item);
                        }
                    }
                    return list;
                })
                .Select(list => list as IReadOnlyList<MeshNode>)
            : Observable.Return<IReadOnlyList<MeshNode>>(Array.Empty<MeshNode>());

        var editStateId = $"editState_comment_{hubPath.Replace("/", "_")}";
        var initialized = new[] { false };

        // Check permissions once (parent path for comment permissions)
        var parentPath = hubPath.Contains('/') ? hubPath[..hubPath.LastIndexOf('/')] : hubPath;
        var permissionsStream = Observable.FromAsync(() => PermissionHelper.GetEffectivePermissionsAsync(host.Hub, parentPath));

        return nodeStream
            .CombineLatest(repliesStream, permissionsStream, (nodes, replies, perms) =>
            {
                var node = nodes.FirstOrDefault(n => n.Path == hubPath);
                var canComment = perms.HasFlag(Permission.Comment) || perms.HasFlag(Permission.Update);
                var canDelete = perms.HasFlag(Permission.Delete);
                return BuildOverview(host, node, hubPath, editStateId, initialized,
                    replies ?? Array.Empty<MeshNode>(), currentUser, canComment, canDelete);
            });
    }

    /// <summary>
    /// Renders the Edit area for a Comment node.
    /// Uses BuildPropertyOverview for auto-generated MarkdownEditor on the Text field.
    /// Only the comment author can edit; others see a read-only message.
    /// </summary>
    public static IObservable<UiControl?> Edit(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var currentUser = accessService?.Context?.Name ?? "";

        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return<MeshNode[]>(Array.Empty<MeshNode>());

        return nodeStream.SelectMany(async nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            var canComment = await PermissionHelper.CanCommentAsync(host.Hub, hubPath);
            return (UiControl?)BuildEditContent(host, node, hubPath, currentUser, canComment);
        });
    }

    internal static UiControl BuildOverview(LayoutAreaHost host, MeshNode? node, string hubPath,
        string editStateId, bool[] initialized, IReadOnlyList<MeshNode> replyNodes, string currentUser,
        bool canComment = true, bool canDelete = true)
    {
        var comment = node?.Content as Comment;
        if (comment == null)
        {
            return Controls.Html("<div style=\"color: var(--neutral-foreground-hint); padding: 8px;\">No comment content</div>");
        }

        // Permission + author check: need Comment permission AND (author match OR !AuthorEditOnly)
        var isAuthor = string.Equals(comment.Author, currentUser, StringComparison.OrdinalIgnoreCase);
        var canEdit = canComment && (!CommentNodeType.AuthorEditOnly || isAuthor);

        var hasContent = !string.IsNullOrWhiteSpace(comment.Text);

        var container = Controls.Stack.WithWidth("100%");

        // Author header row
        var author = comment.Author;
        container = container.WithView(Controls.Html($@"
            <div style=""display: flex; align-items: center; gap: 6px; margin-bottom: 4px;"">
                <span style=""font-weight: 600; font-size: 0.85rem;"">{System.Web.HttpUtility.HtmlEncode(author)}</span>
                <span style=""font-size: 0.75rem; color: var(--neutral-foreground-hint);"">{comment.CreatedAt:g}</span>
            </div>"));

        // Toggleable content: click to edit (author only), Done to return
        if (canEdit)
        {
            container = container.WithView((h, _) =>
            {
                // Initialize edit state once
                if (!initialized[0])
                {
                    h.UpdateData(editStateId, !hasContent);
                    initialized[0] = true;
                }

                return h.Stream.GetDataStream<bool>(editStateId)
                    .DistinctUntilChanged()
                    .Select(isEditing => isEditing
                        ? BuildCommentEditor(h, hubPath, comment.Text, editStateId)
                        : BuildCommentReadOnly(comment.Text, editStateId));
            });
        }
        else
        {
            // Read-only for non-authors
            if (!string.IsNullOrWhiteSpace(comment.Text))
            {
                container = container.WithView(new MarkdownControl(comment.Text)
                    .WithStyle("font-size: 0.85rem; line-height: 1.4;"));
            }
        }

        // Replies section — expandable with load-more
        if (replyNodes.Count > 0)
        {
            container = container.WithView(BuildRepliesSection(host, hubPath, replyNodes));
        }

        return container;
    }

    private static UiControl BuildRepliesSection(LayoutAreaHost host, string hubPath,
        IReadOnlyList<MeshNode> replyNodes)
    {
        var expandedStateId = $"replies_expanded_{hubPath.Replace("/", "_")}";
        var visibleCountStateId = $"replies_visible_{hubPath.Replace("/", "_")}";

        var replyPaths = replyNodes
            .Where(n => n.Content is Comment)
            .OrderBy(n => ((Comment)n.Content!).CreatedAt)
            .Select(n => n.Path)
            .ToList();

        var totalCount = replyPaths.Count;

        // Use WithView callback so state initialization happens with the actual host
        return Controls.Stack.WithWidth("100%").WithStyle("margin-top: 8px;")
            .WithView((h, _) =>
            {
                // Initialize state on first render
                h.UpdateData(expandedStateId, false);
                h.UpdateData(visibleCountStateId, InitialReplyCount);

                return h.Stream.GetDataStream<bool>(expandedStateId)
                    .CombineLatest(
                        h.Stream.GetDataStream<int>(visibleCountStateId),
                        (expanded, visibleCount) =>
                        {
                            var section = Controls.Stack.WithWidth("100%");

                            // Toggle header
                            var headerText = expanded
                                ? $"▾ Replies ({totalCount})"
                                : $"▸ Replies ({totalCount})";
                            section = section.WithView(Controls.Html(
                                    $"<span style=\"font-size: 0.8rem; font-weight: 600; color: var(--accent-fill-rest); cursor: pointer;\">{headerText}</span>")
                                .WithClickAction(ctx =>
                                {
                                    ctx.Host.UpdateData(expandedStateId, !expanded);
                                    return Task.CompletedTask;
                                }));

                            if (!expanded)
                                return (UiControl)section;

                            // Show reply Overview areas up to visibleCount
                            var shown = Math.Min(visibleCount, totalCount);
                            for (var i = 0; i < shown; i++)
                            {
                                var replyAddress = replyPaths[i];
                                section = section.WithView(Controls.Stack
                                    .WithStyle("margin-left: 12px; padding-left: 8px; border-left: 2px solid var(--neutral-stroke-rest); margin-top: 6px;")
                                    .WithView(Controls.LayoutArea(replyAddress, OverviewArea)));
                            }

                            // Load more button
                            if (shown < totalCount)
                            {
                                var remaining = totalCount - shown;
                                section = section.WithView(Controls.Button(
                                        $"Load {Math.Min(remaining, LoadMoreCount)} more repl{(Math.Min(remaining, LoadMoreCount) == 1 ? "y" : "ies")}")
                                    .WithAppearance(Appearance.Lightweight)
                                    .WithStyle("margin-top: 4px; margin-left: 12px; font-size: 0.8rem;")
                                    .WithClickAction(ctx =>
                                    {
                                        ctx.Host.UpdateData(visibleCountStateId, visibleCount + LoadMoreCount);
                                        return Task.CompletedTask;
                                    }));
                            }

                            return section;
                        });
            });
    }

    private static UiControl BuildCommentReadOnly(string text, string editStateId)
    {
        var view = Controls.Stack
            .WithWidth("100%")
            .WithStyle("cursor: pointer;");

        if (!string.IsNullOrWhiteSpace(text))
        {
            view = view.WithView(new MarkdownControl(text)
                .WithStyle("font-size: 0.85rem; line-height: 1.4;"));
        }
        else
        {
            view = view.WithView(
                Controls.Html("<p style=\"color: var(--neutral-foreground-hint); font-style: italic; font-size: 0.85rem;\">Click to add comment text...</p>"));
        }

        view = view.WithClickAction(ctx =>
        {
            ctx.Host.UpdateData(editStateId, true);
            return Task.CompletedTask;
        });

        return view;
    }

    private static UiControl BuildCommentEditor(LayoutAreaHost host, string hubPath, string text, string editStateId)
    {
        var stack = Controls.Stack.WithWidth("100%");

        // Editor
        var editor = new MarkdownEditorControl()
            .WithDocumentId(hubPath)
            .WithValue(text ?? "")
            .WithHeight("150px")
            .WithPlaceholder("Write your comment...")
            .WithAutoSave(host.Hub.Address.ToString(), hubPath);
        stack = stack.WithView(editor);

        // Done button
        stack = stack.WithView(Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("justify-content: flex-end; margin-top: 4px;")
            .WithView(Controls.Button("Done")
                .WithAppearance(Appearance.Accent)
                .WithClickAction(ctx =>
                {
                    ctx.Host.UpdateData(editStateId, false);
                    return Task.CompletedTask;
                })));

        return stack;
    }

    /// <summary>
    /// Builds the action menu (Edit / Delete) for a comment, gated by permissions.
    /// </summary>
    private static UiControl BuildCommentActionMenu(LayoutAreaHost host, string hubPath, bool canComment, bool canDelete)
    {
        var menu = Controls.MenuItem("", FluentIcons.MoreHorizontal(IconSize.Size20))
            .WithAppearance(Appearance.Stealth)
            .WithIconOnly();

        // Edit option (requires Comment permission)
        if (canComment)
        {
            var editHref = MeshNodeLayoutAreas.BuildContentUrl(hubPath, EditArea);
            menu = menu.WithView(new NavLinkControl("Edit", FluentIcons.Edit(IconSize.Size16), editHref));
        }

        // Delete option (requires Delete permission)
        if (canDelete)
        {
            menu = menu.WithView(
                Controls.MenuItem("Delete", FluentIcons.Delete(IconSize.Size16))
                    .WithClickAction(async _ =>
                    {
                        var meshCatalog = host.Hub.ServiceProvider.GetRequiredService<IMeshCatalog>();
                        await meshCatalog.DeleteNodeAsync(hubPath, recursive: true);
                    }));
        }

        return menu;
    }

    /// <summary>
    /// Builds the Edit content for a Comment node.
    /// Uses BuildPropertyOverview for auto-generated MarkdownEditor on the Text field.
    /// </summary>
    private static UiControl BuildEditContent(LayoutAreaHost host, MeshNode? node, string hubPath, string currentUser, bool canComment = true)
    {
        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 16px; max-width: 600px;");

        if (node == null)
        {
            return stack.WithView(Controls.Html("<p style=\"color: var(--warning-color);\">Comment not found.</p>"));
        }

        var comment = node.Content as Comment;

        // Permission check — need Comment permission
        if (!canComment)
        {
            return stack.WithView(Controls.Html("<p style=\"color: var(--neutral-foreground-hint);\">You do not have permission to edit comments.</p>"))
                .WithView(Controls.Button("Back")
                    .WithAppearance(Appearance.Lightweight)
                    .WithIconStart(FluentIcons.ArrowLeft())
                    .WithNavigateToHref(MeshNodeLayoutAreas.BuildContentUrl(hubPath, OverviewArea)));
        }

        // Author check — only the author can edit (when AuthorEditOnly is enabled)
        if (comment != null && CommentNodeType.AuthorEditOnly && !string.IsNullOrEmpty(currentUser)
            && !string.Equals(comment.Author, currentUser, StringComparison.OrdinalIgnoreCase))
        {
            return stack.WithView(Controls.Html("<p style=\"color: var(--neutral-foreground-hint);\">You can only edit your own comments.</p>"))
                .WithView(Controls.Button("Back")
                    .WithAppearance(Appearance.Lightweight)
                    .WithIconStart(FluentIcons.ArrowLeft())
                    .WithNavigateToHref(MeshNodeLayoutAreas.BuildContentUrl(hubPath, OverviewArea)));
        }

        // Header with Done button
        var doneHref = MeshNodeLayoutAreas.BuildContentUrl(hubPath, OverviewArea);

        stack = stack.WithView(Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap(16)
            .WithStyle("align-items: center; margin-bottom: 16px; justify-content: space-between;")
            .WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithHorizontalGap(16)
                .WithStyle("align-items: center;")
                .WithView(Controls.Button("Done")
                    .WithAppearance(Appearance.Lightweight)
                    .WithIconStart(FluentIcons.ArrowLeft())
                    .WithNavigateToHref(doneHref))
                .WithView(Controls.H2("Edit Comment").WithStyle("margin: 0;"))));

        // Property editor (auto-generates MarkdownEditorControl for [Markdown] Text field)
        if (node.Content != null)
        {
            stack = stack.WithView(OverviewLayoutArea.BuildPropertyOverview(host, node));
        }

        return stack;
    }

    /// <summary>
    /// Renders a compact thumbnail for comment nodes.
    /// </summary>
    public static IObservable<UiControl?> Thumbnail(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return<MeshNode[]>(Array.Empty<MeshNode>());

        return nodeStream.Select(nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            return BuildThumbnail(node);
        });
    }

    /// <summary>
    /// Builds a collapsible "Replies (N)" catalog from reply MeshNodes.
    /// Each reply is rendered as a Thumbnail.
    /// </summary>
    internal static UiControl BuildRepliesCatalog(IReadOnlyList<MeshNode> replyNodes)
    {
        var items = replyNodes.Select(r => (UiControl)BuildThumbnail(r)).ToImmutableList();
        return new CatalogControl
            {
                CollapsibleSections = true,
                ShowCounts = true,
                Xs = 12, Sm = 12, Md = 12, Lg = 12,
                CardHeight = 60,
                Spacing = 1,
                SectionGap = 0,
            }
            .WithGroup(new CatalogGroup
            {
                Key = "replies",
                Label = "Replies",
                IsExpanded = false,
                TotalCount = replyNodes.Count,
                Items = items,
            });
    }

    internal static UiControl BuildThumbnail(MeshNode? node)
    {
        var comment = node?.Content as Comment;
        var author = comment?.Author ?? "Unknown";
        var preview = comment?.Text ?? "";
        if (preview.Length > 50) preview = preview[..47] + "...";

        return Controls.Stack
            .WithStyle("padding: 8px 12px; background: var(--neutral-layer-card-container); border: 1px solid var(--neutral-stroke-rest); border-radius: 6px;")
            .WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("align-items: center; gap: 8px;")
                .WithView(Controls.Icon(FluentIcons.Comment(IconSize.Size16)).WithStyle("color: #3b82f6;"))
                .WithView(Controls.Html($"<span style=\"font-size: 0.85rem; font-weight: 500;\">{System.Web.HttpUtility.HtmlEncode(author)}</span>")))
            .WithView(Controls.Html($"<p style=\"margin: 4px 0 0 0; font-size: 0.85rem; color: var(--neutral-foreground-hint); overflow: hidden; text-overflow: ellipsis; white-space: nowrap;\">{System.Web.HttpUtility.HtmlEncode(preview)}</p>"));
    }
}
