using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// Provides dedicated views for Comment nodes.
/// Shows author, highlighted text quote, comment text, status, and child replies.
/// </summary>
public static class CommentLayoutAreas
{
    /// <summary>Area name for the Overview layout area.</summary>
    public const string OverviewArea = "Overview";
    /// <summary>Area name for the Edit layout area.</summary>
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
    /// Formats a DateTimeOffset as a relative time string (e.g., "2m", "3h", "5d", "2mo", "1y").
    /// </summary>
    internal static string FormatRelativeTime(DateTimeOffset created)
    {
        var elapsed = DateTimeOffset.UtcNow - created;
        if (elapsed.TotalMinutes < 1) return "now";
        if (elapsed.TotalMinutes < 60) return $"{(int)elapsed.TotalMinutes}m";
        if (elapsed.TotalHours < 24) return $"{(int)elapsed.TotalHours}h";
        if (elapsed.TotalDays < 30) return $"{(int)elapsed.TotalDays}d";
        if (elapsed.TotalDays < 365) return $"{(int)(elapsed.TotalDays / 30)}mo";
        return $"{(int)(elapsed.TotalDays / 365)}y";
    }

    /// <summary>
    /// Renders the Overview area for a Comment node.
    /// Shows author, comment text (as rendered markdown), click-to-edit toggle,
    /// and expandable replies section with load-more.
    /// </summary>
    public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var currentUser = accessService?.Context?.Name ?? "";

        var editStateId = $"editState_comment_{hubPath.Replace("/", "_")}";
        var initialized = new[] { false, false, false }; // [0]=editState, [1]=repliesExpanded, [2]=replyForm

        var parentPath = hubPath.Contains('/') ? hubPath[..hubPath.LastIndexOf('/')] : hubPath;
        var permissionsStream = host.Hub.GetEffectivePermissions(parentPath);

        // Replies are the direct-CHILD Comment nodes of THIS comment ({hubPath}/{replyId}). Discover
        // them reactively with the SAME synced-query pattern the page-level comment list uses
        // (CommentsView.Comments) — write path (CreateNode a child) and read path (query children)
        // therefore agree on ONE canonical namespace. The old renderer read comment.Replies, a
        // denormalized list nothing on the write path maintained (the ↩ form never appended to it and
        // the sample data left it empty), so every reply was invisible (issue #473). Ordered oldest-first
        // for natural conversation order.
        var repliesDataId = $"commentReplies_{hubPath.Replace("/", "_")}";
        host.UpdateData(repliesDataId, Array.Empty<LayoutAreaControl>());
        host.Workspace.GetQuery(
                $"replies:{hubPath}",
                $"namespace:{hubPath} nodeType:{CommentNodeType.NodeType}")
            .Subscribe(snapshot =>
            {
                var replyControls = snapshot
                    .Where(n => n.Content is Comment)
                    .OrderBy(n => n.ContentAs<Comment>(host.Hub.JsonSerializerOptions)!.CreatedAt)
                    .Select(n => Controls.LayoutArea(n.Path, OverviewArea))
                    .ToArray();
                host.UpdateData(repliesDataId, replyControls);
            });

        return host.Workspace.GetMeshNodeStream()
            .CombineLatest(permissionsStream, (node, perms) =>
            {
                var canComment = perms.HasFlag(Permission.Comment) || perms.HasFlag(Permission.Update);
                var canDelete = perms.HasFlag(Permission.Delete);
                return (UiControl?)BuildOverview(host, node, hubPath, editStateId, initialized,
                    currentUser, canComment, canDelete, repliesDataId);
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

        var permissionsStream = host.Hub.GetEffectivePermissions(hubPath);

        return host.Workspace.GetMeshNodeStream()
            .CombineLatest(permissionsStream, (node, perms) =>
            {
                var canComment = perms.HasFlag(Permission.Comment) || perms.HasFlag(Permission.Update);
                return (UiControl?)BuildEditContent(host, node, hubPath, currentUser, canComment);
            });
    }

    internal static UiControl BuildOverview(LayoutAreaHost host, MeshNode? node, string hubPath,
        string editStateId, bool[] initialized, string currentUser,
        bool canComment = true, bool canDelete = true, string? repliesDataId = null)
    {
        // ContentAs (deserialize), not `as Comment`: this view is data-bound via CombineLatest on
        // GetMeshNodeStream, whose frames alternate typed↔JsonElement; `as` → null on JsonElement
        // frames would flip the whole overview to the "No comment content" placeholder and back →
        // render storm.
        var comment = node.ContentAs<Comment>(host.Hub.JsonSerializerOptions);
        if (comment == null)
        {
            return Controls.Html("<div style=\"color: var(--neutral-foreground-hint); padding: 8px;\">No comment content</div>");
        }

        // Permission + author check: need Comment permission AND (author match OR !AuthorEditOnly)
        var isAuthor = string.Equals(comment.Author, currentUser, StringComparison.OrdinalIgnoreCase);
        var canEdit = canComment && (!CommentNodeType.AuthorEditOnly || isAuthor);
        var canAct = !string.IsNullOrEmpty(currentUser);

        var hasContent = !string.IsNullOrWhiteSpace(comment.Text);
        var isResolved = comment.Status == CommentStatus.Resolved;

        var container = Controls.Stack.WithWidth("100%")
            .WithStyle(isResolved ? "opacity: 0.5;" : "");

        // Header: author on the left, time + action buttons right-aligned
        var author = System.Web.HttpUtility.HtmlEncode(comment.Author);
        var relTime = FormatRelativeTime(comment.CreatedAt);

        var headerRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithWidth("100%")
            .WithStyle("align-items: center; justify-content: space-between; margin-bottom: 2px;")
            .WithView(Controls.Html(
                $"<span style=\"font-weight: 600; font-size: 0.85rem; white-space: nowrap;\">{author}</span>"));

        // Right group with time + action buttons
        var rightGroup = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("align-items: center; gap: 4px; flex-shrink: 0;");

        rightGroup = rightGroup.WithView(Controls.Html(
            $"<span style=\"font-size: 0.75rem; color: var(--neutral-foreground-hint); white-space: nowrap;\">{relTime}</span>"));

        if (canEdit)
            rightGroup = rightGroup.WithView(
                Controls.Html("<span style=\"cursor: pointer; font-size: 0.8rem; color: var(--accent-fill-rest);\" title=\"Edit\">✎</span>")
                    .WithClickAction(ctx =>
                    {
                        ctx.Host.UpdateData(editStateId, true);
                        return Task.CompletedTask;
                    }));
        if (canAct)
            rightGroup = rightGroup.WithView(BuildReplyButton(host, hubPath));
        if (canAct && !isResolved && IsTopLevelComment(hubPath, comment))
            rightGroup = rightGroup.WithView(BuildResolveButton(host, hubPath, comment));
        if (canDelete || canComment)
            rightGroup = rightGroup.WithView(BuildDeleteButton(host, hubPath));

        headerRow = headerRow.WithView(rightGroup);

        container = container.WithView(headerRow);

        // Toggleable content: click to edit (author only), Done to return
        if (canEdit)
        {
            container = container.WithView((h, _) =>
            {
                // Initialize edit state once
                if (!initialized[0])
                {
                    h.UpdateData(editStateId, false);
                    initialized[0] = true;
                }

                return h.Stream.GetDataStream<bool>(editStateId)
                    .DistinctUntilChanged()
                    .Select(isEditing => isEditing
                        ? BuildCommentEditor(h, hubPath, comment.Text, editStateId)
                        : BuildCommentReadOnly(comment.Text));
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

        // Inline reply create-form — surfaces when the ↩ Reply button writes the replyPath_* data item
        // (see BuildReplyButton). Mirrors the edit toggle above: the click sets a data item, this view
        // reacts and renders the editor. Without this wiring the ↩ click created a transient reply node
        // but no editor ever rendered — BuildReplyCreateForm was orphaned (defined, never called). Gated
        // on canAct (a logged-in user), matching the Reply button's own visibility gate. When no reply
        // is in progress the view emits null → the area renders nothing (cleared after Cancel/Create).
        if (canAct)
        {
            var replyPathStateId = $"replyPath_{hubPath.Replace("/", "_")}";
            container = container.WithView((h, _) =>
            {
                if (!initialized[2])
                {
                    h.UpdateData(replyPathStateId, "");
                    initialized[2] = true;
                }

                return h.Stream.GetDataStream<string>(replyPathStateId)
                    .DistinctUntilChanged()
                    .Select(replyPath => string.IsNullOrEmpty(replyPath)
                        ? (UiControl?)null
                        : BuildReplyCreateForm(h, replyPath, replyPathStateId, comment, currentUser));
            });
        }

        // Replies section — data-bound from the live child-Comment query set up in Overview
        // (repliesDataId). Each entry is a LayoutArea pointing at a reply's own path
        // ({hubPath}/{replyId}), so a reply created through EITHER write path (the ↩ form or the
        // CreateCommentRequest handler) renders here for every reader — the fix for issue #473. The
        // section appears/disappears reactively with the query (no static comment.Replies gate).
        if (repliesDataId != null)
        {
            var expandedStateId = $"replies_expanded_{hubPath.Replace("/", "_")}";
            var visibleCountStateId = $"replies_visible_{hubPath.Replace("/", "_")}";

            container = container.WithView((h, _) =>
            {
                if (!initialized[1])
                {
                    h.UpdateData(expandedStateId, false);
                    h.UpdateData(visibleCountStateId, InitialReplyCount);
                    initialized[1] = true;
                }

                return h.Stream.GetDataStream<LayoutAreaControl[]>(repliesDataId)
                    .CombineLatest(
                        h.Stream.GetDataStream<bool>(expandedStateId),
                        h.Stream.GetDataStream<int>(visibleCountStateId),
                        (replyControls, expanded, visibleCount) =>
                        {
                            var totalCount = replyControls?.Length ?? 0;
                            if (totalCount == 0)
                                return (UiControl)Controls.Stack;

                            var section = Controls.Stack.WithWidth("100%").WithStyle("margin-top: 8px;");

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

                            var shown = Math.Min(visibleCount, totalCount);
                            for (var i = 0; i < shown; i++)
                            {
                                section = section.WithView(Controls.Stack
                                    .WithStyle("margin-left: 0; padding-left: 8px; border-left: 2px solid var(--neutral-stroke-rest); margin-top: 4px;")
                                    .WithView(replyControls![i]));
                            }

                            if (shown < totalCount)
                            {
                                var remaining = totalCount - shown;
                                section = section.WithView(Controls.Button(
                                        $"Load {Math.Min(remaining, LoadMoreCount)} more repl{(Math.Min(remaining, LoadMoreCount) == 1 ? "y" : "ies")}")
                                    .WithAppearance(Appearance.Lightweight)
                                    .WithStyle("margin-top: 4px; margin-left: 4px; font-size: 0.8rem;")
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

        return container;
    }


    private static UiControl BuildCommentReadOnly(string text)
    {
        var view = Controls.Stack
            .WithWidth("100%");

        if (!string.IsNullOrWhiteSpace(text))
        {
            view = view.WithView(new MarkdownControl(text)
                .WithStyle("font-size: 0.85rem; line-height: 1.4;"));
        }
        else
        {
            view = view.WithView(
                Controls.Html("<p style=\"color: var(--neutral-foreground-hint); font-style: italic; font-size: 0.85rem;\">No comment text</p>"));
        }

        return view;
    }

    private static UiControl BuildCommentEditor(LayoutAreaHost host, string hubPath, string text, string editStateId)
    {
        var stack = Controls.Stack.WithWidth("100%");
        var textDataId = $"commentText_{hubPath.Replace("/", "_")}";

        // Initialize text data area with current comment text
        host.UpdateData(textDataId, new Dictionary<string, object?> { ["text"] = text ?? "" });

        // Editor bound to data area — no WithAutoSave() to avoid overwriting
        // the Comment node with a Markdown node (which saves as .md instead of .json)
        var editor = new MarkdownEditorControl()
            .WithDocumentId(hubPath)
            .WithHeight("150px")
            .WithPlaceholder("Write your comment...") with
        {
            Value = new JsonPointerReference("text"),
            DataContext = LayoutAreaReference.GetDataPointer(textDataId)
        };
        stack = stack.WithView(editor);

        // Done button — saves the comment text via persistence then switches to readonly
        stack = stack.WithView(Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("justify-content: flex-end; margin-top: 4px;")
            .WithView(Controls.Button("Done")
                .WithAppearance(Appearance.Accent)
                .WithClickAction(ctx =>
                {
                    // Read text, then resolve the comment via MeshNodeReference on its own hub
                    // (AsynchronousCalls.md: known-path read goes via MeshNodeReference, never QueryAsync).
                    ctx.Host.Stream.GetDataStream<Dictionary<string, object?>>(textDataId)
                        .Take(1)
                        .Subscribe(data =>
                        {
                            var newText = data?.GetValueOrDefault("text")?.ToString() ?? "";

                            var ownPath = host.Hub.Address.Path;
                            var cache = host.Hub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
                            cache.Update(ownPath, n =>
                            {
                                var existing = n.ContentAs<Comment>(host.Hub.JsonSerializerOptions);
                                // Existing node whose content can't be recovered → leave it alone, NEVER clobber.
                                if (n.Content is not null && existing is null)
                                    return n;
                                existing ??= new Comment();
                                return n with { Content = existing with { Text = newText } };
                            }, host.Hub.JsonSerializerOptions).Subscribe(
                                _ => ctx.Host.UpdateData(editStateId, false),
                                _ => ctx.Host.UpdateData(editStateId, false));
                        });
                    return Task.CompletedTask;
                })));

        return stack;
    }

    /// <summary>
    /// Builds the inline reply creation form with markdown editor, Cancel, and Create buttons.
    /// Cancel just closes the draft form (nothing was persisted).
    /// Create writes the reply Active with its text in a single <c>CreateNode</c>.
    /// </summary>
    private static UiControl BuildReplyCreateForm(LayoutAreaHost host, string replyPath, string replyPathStateId,
        Comment comment, string currentUser)
    {
        var nodeFactory = host.Hub.ServiceProvider.GetRequiredService<IMeshService>();
        var replyTextDataId = $"replyText_{replyPath.Replace("/", "_")}";
        var replyErrorDataId = $"replyError_{replyPath.Replace("/", "_")}";

        host.UpdateData(replyTextDataId, new Dictionary<string, object?> { ["text"] = "" });
        host.UpdateData(replyErrorDataId, "");

        var stack = Controls.Stack
            .WithStyle("margin-top: 4px; padding: 4px; padding-left: 6px; border-left: 2px solid var(--accent-fill-rest);");

        // Markdown editor bound to local data area
        var editor = new MarkdownEditorControl()
            .WithDocumentId(replyPath)
            .WithHeight("100px")
            .WithPlaceholder("Write your reply...") with
        {
            Value = new JsonPointerReference("text"),
            DataContext = LayoutAreaReference.GetDataPointer(replyTextDataId)
        };
        stack = stack.WithView(editor);

        // User-visible error surface: a failed reply write (e.g. Access denied) must NOT be a
        // server-only log line while the form closes as if it succeeded (issue #473, defect 3).
        // The Create click writes the message here on failure and clears it on retry.
        stack = stack.WithView((h, _) => h.Stream.GetDataStream<string>(replyErrorDataId)
            .Select(err => string.IsNullOrEmpty(err)
                ? (UiControl)Controls.Stack
                : Controls.Html(
                    $"<div style=\"color: var(--error-color, #f87171); font-size: 0.8rem; margin-top: 4px;\">{System.Web.HttpUtility.HtmlEncode(err)}</div>")));

        // Button row: Cancel and Create
        stack = stack.WithView(Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap(12)
            .WithStyle("margin-top: 8px; justify-content: flex-end;")
            .WithView(Controls.Button("Cancel")
                .WithAppearance(Appearance.Neutral)
                .WithClickAction(_ =>
                {
                    // Nothing was persisted — the draft lives only in the form's data stream.
                    host.UpdateData(replyPathStateId, "");
                    return Task.CompletedTask;
                }))
            .WithView(Controls.Button("Create")
                .WithAppearance(Appearance.Accent)
                .WithIconStart(FluentIcons.Add())
                .WithClickAction(ctx =>
                {
                    // Read the draft text, then write the reply with ONE CreateNode (Active, through the
                    // access-control pipeline). No transient placeholder — the reply is authored at its
                    // own replyPath, a child of the comment being replied to.
                    ctx.Host.Stream.GetDataStream<Dictionary<string, object?>>(replyTextDataId)
                        .Take(1)
                        .Subscribe(data =>
                        {
                            var text = data?.GetValueOrDefault("text")?.ToString() ?? "";
                            var replyId = replyPath.Split('/').Last();
                            var replyNamespace = replyPath[..replyPath.LastIndexOf('/')];
                            var replyNode = new MeshNode(replyId, replyNamespace)
                            {
                                Name = $"Reply to {comment.Author}",
                                NodeType = CommentNodeType.NodeType,
                                State = MeshNodeState.Active,
                                // MainNode = the document, so SatelliteAccessRule delegates the reply's
                                // permissions to the doc (Comment to create, author/Update to delete) —
                                // exactly like a top-level comment.
                                MainNode = comment.PrimaryNodePath ?? "",
                                Content = new Comment
                                {
                                    Id = replyId,
                                    PrimaryNodePath = comment.PrimaryNodePath,
                                    Author = currentUser,
                                    Status = CommentStatus.Active,
                                    Text = text
                                }
                            };
                            host.UpdateData(replyErrorDataId, "");
                            nodeFactory.CreateNode(replyNode).Subscribe(
                                _ => host.UpdateData(replyPathStateId, ""),
                                // Keep the draft form OPEN on failure (don't clear the state) so the user's
                                // reply isn't silently lost; surface the error in the form AND log it.
                                ex =>
                                {
                                    host.UpdateData(replyErrorDataId, $"Reply not posted: {ex.Message}");
                                    host.Hub.ServiceProvider.GetService<ILogger<LayoutAreaHost>>()
                                        ?.LogWarning(ex, "Failed to create reply at {Path}", replyPath);
                                });
                        });
                    return Task.CompletedTask;
                })));

        return stack;
    }

    /// <summary>
    /// Builds the Reply icon button. Opens the inline reply form for a fresh reply path — NOTHING is
    /// persisted until the user clicks Create (then one <c>CreateNode</c> writes the reply Active).
    /// No transient placeholder is written, so an unsent draft never appears and there is no
    /// PG-invisible node to resolve.
    /// </summary>
    private static UiControl BuildReplyButton(LayoutAreaHost host, string hubPath)
    {
        return Controls.Html("<span style=\"cursor: pointer; font-size: 0.8rem; color: var(--accent-fill-rest);\" title=\"Reply\">↩</span>")
            .WithClickAction(_ =>
            {
                var replyId = Guid.NewGuid().AsString();
                var replyPath = $"{hubPath}/{replyId}";
                var replyPathStateId = $"replyPath_{hubPath.Replace("/", "_")}";
                var expandedStateId = $"replies_expanded_{hubPath.Replace("/", "_")}";
                host.UpdateData(expandedStateId, true);
                host.UpdateData(replyPathStateId, replyPath);
                return Task.CompletedTask;
            });
    }

    /// <summary>
    /// Builds the Resolve (checkmark) icon button. Marks the comment as Resolved.
    /// Markers are kept in the document so resolved comments remain visible (grayed out) in the sidebar.
    /// </summary>
    private static UiControl BuildResolveButton(LayoutAreaHost host, string hubPath, Comment comment)
    {
        return Controls.Html("<span style=\"cursor: pointer; font-size: 0.8rem; color: #4ade80;\" title=\"Resolve\">✓</span>")
            .WithClickAction(_ =>
            {
                // Resolve comment — write through the shared cache so every
                // reader of the comment's stream sees the status flip.
                var ownPath = host.Hub.Address.Path;
                var cache = host.Hub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
                cache.Update(ownPath, n =>
                {
                    var c = n.ContentAs<Comment>(host.Hub.JsonSerializerOptions) ?? comment;
                    return n with { Content = c with { Status = CommentStatus.Resolved } };
                }, host.Hub.JsonSerializerOptions).Subscribe(
                    _ => { },
                    ex => host.Hub.ServiceProvider.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()
                        ?.CreateLogger(typeof(CommentLayoutAreas).FullName!)
                        .LogWarning(ex, "Comment resolve failed for {Path}", ownPath));
                return Task.CompletedTask;
            });
    }

    /// <summary>
    /// Returns true if this comment is a top-level comment (direct child of the _Comment partition),
    /// false if it is a reply (child of another comment).
    /// Top-level comments live at {PrimaryNodePath}/_Comment/{id} (parent = {PrimaryNodePath}/_Comment).
    /// Replies live at {PrimaryNodePath}/_Comment/{commentId}/{replyId} (parent = deeper path).
    /// </summary>
    public static bool IsTopLevelComment(string hubPath, Comment comment)
    {
        if (string.IsNullOrEmpty(comment.PrimaryNodePath))
            return true;
        var parentPath = hubPath.Contains('/') ? hubPath[..hubPath.LastIndexOf('/')] : hubPath;
        var expectedTopLevel = $"{comment.PrimaryNodePath}/{CommentsExtensions.CommentPartition}";
        return string.Equals(parentPath, expectedTopLevel, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds the Delete (trash) icon button. Deletes the comment node recursively.
    /// </summary>
    private static UiControl BuildDeleteButton(LayoutAreaHost host, string hubPath)
    {
        return Controls.Html("<span style=\"cursor: pointer; font-size: 0.8rem; color: #f87171;\" title=\"Delete\">✕</span>")
            .WithClickAction(_ =>
            {
                var nodeFactory = host.Hub.ServiceProvider.GetRequiredService<IMeshService>();
                // Never swallow the delete fault: a denied/failed delete must be diagnosable, not a
                // silent no-op (issue #391). The comment stays visible on failure; the log is the sink.
                nodeFactory.DeleteNode(hubPath).Subscribe(
                    __ => { },
                    ex => host.Hub.ServiceProvider.GetService<ILogger<LayoutAreaHost>>()
                        ?.LogWarning(ex, "Failed to delete comment at {Path}", hubPath));
                return Task.CompletedTask;
            });
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
            var editHref = MeshNodeLayoutAreas.BuildUrl(hubPath, EditArea);
            menu = menu.WithView(new NavLinkControl("Edit", FluentIcons.Edit(IconSize.Size16), editHref));
        }

        // Delete option (requires Delete permission)
        if (canDelete)
        {
            menu = menu.WithView(
                Controls.MenuItem("Delete", FluentIcons.Delete(IconSize.Size16))
                    .WithClickAction(_ =>
                    {
                        var nodeFactory = host.Hub.ServiceProvider.GetRequiredService<IMeshService>();
                        nodeFactory.DeleteNode(hubPath).Subscribe(
                            __ => { },
                            _ => { });
                        return Task.CompletedTask;
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

        // ContentAs (deserialize), not `as Comment`: this view is data-bound via CombineLatest on
        // GetMeshNodeStream; `as` → null on the JsonElement frames flips the author/permission gates
        // and re-renders → storm.
        var comment = node.ContentAs<Comment>(host.Hub.JsonSerializerOptions);

        // Permission check — need Comment permission
        if (!canComment)
        {
            return stack.WithView(Controls.Html("<p style=\"color: var(--neutral-foreground-hint);\">You do not have permission to edit comments.</p>"))
                .WithView(Controls.Button("Back")
                    .WithAppearance(Appearance.Lightweight)
                    .WithIconStart(FluentIcons.ArrowLeft())
                    .WithNavigateToHref(MeshNodeLayoutAreas.BuildUrl(hubPath, OverviewArea)));
        }

        // Author check — only the author can edit (when AuthorEditOnly is enabled)
        if (comment != null && CommentNodeType.AuthorEditOnly && !string.IsNullOrEmpty(currentUser)
            && !string.Equals(comment.Author, currentUser, StringComparison.OrdinalIgnoreCase))
        {
            return stack.WithView(Controls.Html("<p style=\"color: var(--neutral-foreground-hint);\">You can only edit your own comments.</p>"))
                .WithView(Controls.Button("Back")
                    .WithAppearance(Appearance.Lightweight)
                    .WithIconStart(FluentIcons.ArrowLeft())
                    .WithNavigateToHref(MeshNodeLayoutAreas.BuildUrl(hubPath, OverviewArea)));
        }

        // Header with Done button
        var doneHref = MeshNodeLayoutAreas.BuildUrl(hubPath, OverviewArea);

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
        return host.Workspace.GetMeshNodeStream()
            .Select(node => BuildThumbnail(node, host.Hub.JsonSerializerOptions));
    }

    internal static UiControl BuildThumbnail(MeshNode? node, JsonSerializerOptions options)
    {
        // ContentAs (deserialize), not `as Comment`: data-bound via .Select on GetMeshNodeStream,
        // whose frames alternate typed↔JsonElement; `as` → null on JsonElement frames would flip the
        // author/preview text to "Unknown"/empty and back → thumbnail render storm.
        var comment = node.ContentAs<Comment>(options);
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
