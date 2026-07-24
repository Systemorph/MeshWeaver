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
    /// <summary>
    /// Per-layout-area-session view state for the comment Overview. Two jobs:
    /// <list type="bullet">
    ///   <item><b>Once-per-session seeds</b> (<see cref="EditSeeded"/> &amp; friends) — a re-render of
    ///     the Overview (the node stream and the permission stream both emit repeatedly by design)
    ///     must not reset the user's edit/expand state or stomp the text they are typing.</item>
    ///   <item><b>Current-value shadows</b> (<see cref="Editing"/>, <see cref="Text"/>, …) — every
    ///     sub-view stream re-subscribed on a re-render starts with <c>.StartWith(shadow)</c>, because
    ///     the transient <c>/data</c> seed does NOT reliably replay to a second subscription (see the
    ///     same defense in <c>EditorExtensions.BuildToggleableProperty</c>). Without the shadow the
    ///     re-rendered sub-view never emits and the PREVIOUSLY rendered content sticks — the
    ///     "comment only appears after a page refresh" bug.</item>
    /// </list>
    /// Instance state on the host's render pipeline — never static (NoStaticState.md).
    /// </summary>
    internal sealed class OverviewSession
    {
        public bool EditSeeded;
        public bool RepliesSeeded;
        public bool ReplyFormSeeded;

        /// <summary>Shadow of the editState_* data item — is the editor open right now?</summary>
        public bool Editing;
        /// <summary>Shadow of the commentText_* buffer — the text currently displayed/edited.</summary>
        public string Text = "";
        /// <summary>The node text the buffer was last seeded from — external-change detection.</summary>
        public string NodeText = "";

        /// <summary>Shadow of the replyPath_* data item — the open reply draft, "" when none.</summary>
        public string ReplyPath = "";
        /// <summary>The reply path whose draft buffer was already seeded — seed each draft ONCE.</summary>
        public string? SeededReplyPath;

        public LayoutAreaControl[] RepliesControls = Array.Empty<LayoutAreaControl>();
        public bool RepliesExpanded;
        public int RepliesVisible = InitialReplyCount;
    }

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

    /// <summary>A comment with nothing written yet opens straight in EDIT mode — "+ Comment" exists to
    /// write, so a freshly created (empty) comment must not demand an extra ✎ click before typing.
    /// Anything already written renders read-only until the author toggles the editor.</summary>
    internal static bool OpensInEdit(string? text) => string.IsNullOrWhiteSpace(text);

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
        var session = new OverviewSession();

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
                session.RepliesControls = replyControls;
                host.UpdateData(repliesDataId, replyControls);
            });

        return host.Workspace.GetMeshNodeStream()
            .CombineLatest(permissionsStream, (node, perms) =>
            {
                var canComment = perms.HasFlag(Permission.Comment) || perms.HasFlag(Permission.Update);
                var canDelete = perms.HasFlag(Permission.Delete);
                return (UiControl?)BuildOverview(host, node, hubPath, editStateId, session,
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
        string editStateId, OverviewSession session, string currentUser,
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

        // Toggleable content: click to edit (author only), Done to return.
        var textDataId = $"commentText_{hubPath.Replace("/", "_")}";
        // The displayed/edited text lives in ONE session buffer (the commentText_* data item):
        // the editor binds to it, the Done click persists FROM it, and the read-only view renders
        // FROM it. That makes the text the user just saved appear IMMEDIATELY on Done — the old
        // code rendered the comment.Text captured at the LAST node emission, so the fresh text
        // only showed up once the node stream re-emitted AND the /data replay reached the
        // re-rendered sub-view; under production timing neither is guaranteed, and the comment
        // only appeared after a full page refresh (add AND edit — the UWDeepfield report).
        if (canEdit)
        {
            container = container.WithView((h, _) =>
            {
                if (!session.EditSeeded)
                {
                    // Seed once — a fresh (empty) comment opens straight in the editor.
                    session.Editing = OpensInEdit(comment.Text);
                    session.Text = comment.Text ?? "";
                    session.NodeText = comment.Text ?? "";
                    h.UpdateData(textDataId, new Dictionary<string, object?> { ["text"] = session.Text });
                    h.UpdateData(editStateId, session.Editing);
                    session.EditSeeded = true;
                }
                else if (!session.Editing && (comment.Text ?? "") != session.NodeText)
                {
                    // The node's text changed under us (another user / another surface) while we
                    // are NOT editing → refresh the buffer. NEVER while editing — a re-render
                    // mid-edit must not stomp what the user is typing (the old per-render re-seed
                    // in BuildCommentEditor did exactly that).
                    session.NodeText = comment.Text ?? "";
                    session.Text = session.NodeText;
                    h.UpdateData(textDataId, new Dictionary<string, object?> { ["text"] = session.Text });
                }

                // StartWith(shadow): a re-subscribed sub-view gets no reliable /data replay — it
                // must default to the session's current state and then react (same defense as
                // EditorExtensions.BuildToggleableProperty), or the re-rendered area never emits
                // and the previously rendered (stale) content sticks until a refresh.
                return h.Stream.GetDataStream<bool>(editStateId)
                    .StartWith(session.Editing)
                    .DistinctUntilChanged()
                    .Select(isEditing =>
                    {
                        session.Editing = isEditing;
                        return isEditing
                            // While editing the view is static — keystrokes flow into the buffer
                            // without re-rendering the editor control.
                            ? Observable.Return(BuildCommentEditor(h, hubPath, editStateId, textDataId, session))
                            // Read-only renders LIVE from the buffer, so the text saved by Done
                            // (already in the buffer) shows without any node-stream round-trip.
                            : h.Stream.GetDataStream<Dictionary<string, object?>>(textDataId)
                                .Select(d => d?.GetValueOrDefault("text")?.ToString() ?? "")
                                .StartWith(session.Text)
                                .DistinctUntilChanged()
                                .Select(text =>
                                {
                                    session.Text = text;
                                    return BuildCommentReadOnly(text);
                                });
                    })
                    .Switch();
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
                if (!session.ReplyFormSeeded)
                {
                    h.UpdateData(replyPathStateId, "");
                    session.ReplyFormSeeded = true;
                }

                // StartWith(shadow) — same no-replay defense as the edit toggle above, so an open
                // reply draft survives a re-render instead of the area sticking on stale content.
                return h.Stream.GetDataStream<string>(replyPathStateId)
                    .StartWith(session.ReplyPath)
                    .DistinctUntilChanged()
                    .Select(replyPath =>
                    {
                        session.ReplyPath = replyPath;
                        return string.IsNullOrEmpty(replyPath)
                            ? (UiControl?)null
                            : BuildReplyCreateForm(h, replyPath, replyPathStateId, comment, currentUser, session);
                    });
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
                if (!session.RepliesSeeded)
                {
                    h.UpdateData(expandedStateId, false);
                    h.UpdateData(visibleCountStateId, InitialReplyCount);
                    session.RepliesSeeded = true;
                }

                // StartWith(shadow) on all three inputs — CombineLatest only fires once EVERY
                // source has emitted, so a single missing /data replay after a re-render would
                // freeze the whole replies section on its previous render.
                return h.Stream.GetDataStream<LayoutAreaControl[]>(repliesDataId)
                    .StartWith(session.RepliesControls)
                    .DistinctUntilChanged()
                    .CombineLatest(
                        h.Stream.GetDataStream<bool>(expandedStateId)
                            .StartWith(session.RepliesExpanded).DistinctUntilChanged(),
                        h.Stream.GetDataStream<int>(visibleCountStateId)
                            .StartWith(session.RepliesVisible).DistinctUntilChanged(),
                        (replyControls, expanded, visibleCount) =>
                        {
                            session.RepliesExpanded = expanded;
                            session.RepliesVisible = visibleCount;
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

    private static UiControl BuildCommentEditor(LayoutAreaHost host, string hubPath, string editStateId,
        string textDataId, OverviewSession session)
    {
        var stack = Controls.Stack.WithWidth("100%");

        // 🚨 NO buffer seeding here. The session buffer (textDataId) is seeded ONCE by the edit
        // toggle in BuildOverview and refreshed only on an external node-text change while NOT
        // editing. This method runs on every re-render of the Overview while the editor is open
        // (the node and permission streams emit repeatedly by design) — the unconditional re-seed
        // that used to live here overwrote the text the user was typing with the stale captured
        // node text on every such re-render.

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
                                _ =>
                                {
                                    // Update the session shadows BEFORE flipping to read-only: the
                                    // read-only view starts from session.Text, so the text the user
                                    // just saved renders immediately — no dependency on the node
                                    // stream echoing the write back in time (it does not under
                                    // production timing; that was the "only after refresh" bug).
                                    session.Text = newText;
                                    session.NodeText = newText;
                                    ctx.Host.UpdateData(editStateId, false);
                                },
                                ex =>
                                {
                                    // Keep the editor OPEN on a failed save — flipping to read-only
                                    // would silently discard the user's text. Same contract as the
                                    // reply create-form.
                                    host.Hub.ServiceProvider.GetService<ILogger<LayoutAreaHost>>()
                                        ?.LogWarning(ex, "Failed to save comment at {Path}", ownPath);
                                });
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
        Comment comment, string currentUser, OverviewSession session)
    {
        var nodeFactory = host.Hub.ServiceProvider.GetRequiredService<IMeshService>();
        var replyTextDataId = $"replyText_{replyPath.Replace("/", "_")}";
        var replyErrorDataId = $"replyError_{replyPath.Replace("/", "_")}";

        // Seed each reply draft ONCE. This form is rebuilt on every Overview re-render while the
        // draft is open — an unconditional re-seed would wipe the reply the user is typing.
        if (!string.Equals(session.SeededReplyPath, replyPath, StringComparison.Ordinal))
        {
            session.SeededReplyPath = replyPath;
            host.UpdateData(replyTextDataId, new Dictionary<string, object?> { ["text"] = "" });
            host.UpdateData(replyErrorDataId, "");
        }

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
