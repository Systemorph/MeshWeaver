using System.Reactive.Linq;
using System.Reactive.Subjects;
using Humanizer;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Kernel;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using ViewModeSubject = System.Reactive.Subjects.ISubject<MeshWeaver.Graph.MarkdownLayoutAreas.AnnotationViewMode>;

namespace MeshWeaver.Graph;

/// <summary>
/// Provides dedicated views for Markdown nodes with a clean, document-focused layout.
/// Features:
/// - Readonly markdown content display by default
/// - Menu button with options for Edit, Comments, Attachments, Settings
/// - Clean typography and reading experience
/// </summary>
public static class MarkdownLayoutAreas
{
    public const string ReadArea = "Read";
    public const string EditArea = "Edit";
    public const string MetadataArea = "Metadata";
    public const string NotebookArea = "Notebook";
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
                .WithView(EditArea, MarkdownEdit)
                .WithView(MetadataArea, MetadataView)
                .WithView(NotebookArea, NotebookView)
                .WithView(CommentsArea, CommentsView)
                .WithView(AttachmentsArea, AttachmentsView)
                .WithView(MeshNodeLayoutAreas.SettingsArea, MeshNodeLayoutAreas.Settings)
                .WithView(MeshNodeLayoutAreas.MetadataArea, MeshNodeLayoutAreas.Metadata)
                .WithView(MeshNodeLayoutAreas.ThumbnailArea, Thumbnail));

    /// <summary>
    /// View mode for annotation display.
    /// </summary>
    public enum AnnotationViewMode
    {
        Markup,     // Show all annotations with highlighting
        Clean,      // Hide all markup, show plain text
        Accepted,   // Show document as if all changes were accepted
        Original    // Show document as if all changes were rejected (original)
    }

    /// <summary>
    /// State for the annotation panel including reply dialog state.
    /// </summary>
    public record AnnotationPanelState
    {
        /// <summary>
        /// ID of the annotation currently showing the reply dialog, or null if none.
        /// </summary>
        public string? ReplyingToAnnotationId { get; init; }

        /// <summary>
        /// Path of the reply MeshNode currently being edited inline, or null if none.
        /// </summary>
        public string? EditingReplyPath { get; init; }

        /// <summary>
        /// IDs of annotations that are expanded (collapsed by default).
        /// </summary>
        public IReadOnlySet<string> ExpandedAnnotationIds { get; init; }
            = new HashSet<string>();
    }

    /// <summary>
    /// Model for the reply form.
    /// </summary>
    public record ReplyFormModel
    {
        public string Text { get; init; } = string.Empty;
    }

    /// <summary>
    /// Model for the new comment form (text selection commenting).
    /// </summary>
    public record CommentFormModel
    {
        public string SelectedText { get; init; } = string.Empty;
        public string CommentText { get; init; } = string.Empty;
    }

    /// <summary>
    /// Renders the readonly markdown view with a clean reading experience.
    /// Includes a header with title and action menu.
    /// If there are Markdown child nodes, they are displayed in a separate section.
    /// </summary>
    public static IObservable<UiControl?> ReadView(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var meshQuery = host.Hub.ServiceProvider.GetService<IMeshQuery>();

        // Create a subject for view mode changes
        var viewModeSubject = new BehaviorSubject<AnnotationViewMode>(AnnotationViewMode.Markup);
        host.RegisterForDisposal(viewModeSubject);

        // Create a subject for annotation panel state (reply dialogs, etc.)
        var panelStateSubject = new BehaviorSubject<AnnotationPanelState>(new AnnotationPanelState());
        host.RegisterForDisposal(panelStateSubject);

        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return<MeshNode[]>(Array.Empty<MeshNode>());

        // Query for Markdown child nodes
        var childrenStream = Observable.FromAsync(async () =>
        {
            if (meshQuery == null)
                return Array.Empty<MeshNode>() as IReadOnlyList<MeshNode>;

            try
            {
                return await meshQuery.QueryAsync<MeshNode>($"path:{hubPath} nodeType:{GraphConfigurationExtensions.MarkdownNodeType} scope:children").ToListAsync() as IReadOnlyList<MeshNode>;
            }
            catch
            {
                return Array.Empty<MeshNode>();
            }
        });

        // Query for Comment child MeshNodes
        var commentNodesStream = meshQuery != null
            ? meshQuery.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                $"path:{hubPath} nodeType:{CommentNodeType.NodeType} scope:descendants"))
                .Scan(new List<MeshNode>(), (list, change) =>
                {
                    if (change.ChangeType == QueryChangeType.Initial || change.ChangeType == QueryChangeType.Reset)
                        return change.Items.ToList();
                    // Incremental updates
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

        // Combine node stream with view mode, panel state, children, and comment nodes for reactive updates
        return nodeStream
            .CombineLatest(viewModeSubject, panelStateSubject, childrenStream, commentNodesStream,
                (nodes, viewMode, panelState, children, commentNodes) =>
            {
                var node = nodes.FirstOrDefault(n => n.Path == hubPath);
                return BuildReadView(host, node, viewMode, viewModeSubject, panelState, panelStateSubject,
                    children ?? Array.Empty<MeshNode>(), commentNodes ?? Array.Empty<MeshNode>());
            });
    }

    private static UiControl BuildReadView(
        LayoutAreaHost host,
        MeshNode? node,
        AnnotationViewMode viewMode,
        ViewModeSubject viewModeSubject,
        AnnotationPanelState panelState,
        BehaviorSubject<AnnotationPanelState> panelStateSubject,
        IReadOnlyList<MeshNode> markdownChildren,
        IReadOnlyList<MeshNode>? commentNodes = null)
    {
        var nodePath = node?.Path ?? host.Hub.Address.ToString();

        // Check if content has annotations
        var rawContent = GetMarkdownContent(node);
        var hasAnnotations = !string.IsNullOrEmpty(rawContent) &&
            (rawContent.Contains("<!--comment:") || rawContent.Contains("<!--insert:") || rawContent.Contains("<!--delete:"));

        // Extract annotations for the side panel
        var annotations = hasAnnotations
            ? AnnotationParser.ExtractAnnotations(rawContent)
            : new List<ParsedAnnotation>();

        // Transform content based on view mode
        var content = viewMode switch
        {
            AnnotationViewMode.Clean => AnnotationMarkdownExtension.StripAnnotations(rawContent),
            AnnotationViewMode.Accepted => AnnotationMarkdownExtension.GetAcceptedContent(rawContent),
            AnnotationViewMode.Original => AnnotationMarkdownExtension.GetRejectedContent(rawContent),
            _ => rawContent // Markup mode - keep annotations
        };

        commentNodes ??= Array.Empty<MeshNode>();

        // If we have annotations (inline or MeshNode) and in markup mode, use a split layout (Word-style)
        if (((hasAnnotations && annotations.Count > 0) || commentNodes.Count > 0) && viewMode == AnnotationViewMode.Markup)
        {
            return BuildSplitLayoutWithAnnotations(host, node, content, annotations, viewModeSubject, viewMode, panelState, panelStateSubject, markdownChildren, commentNodes);
        }

        // No annotations or non-markup mode - simple layout with menu positioned at top-right of content
        var container = Controls.Stack
            .WithWidth("100%")
            .WithStyle("max-width: 1100px; margin: 0 auto; padding: 24px; background: var(--neutral-layer-1); position: relative;");

        // Action menu positioned at top-right of content area
        var actionMenu = Controls.Stack
            .WithStyle("position: absolute; top: 24px; right: 24px; z-index: 10;")
            .WithView(BuildActionMenu(host, node));

        container = container.WithView(actionMenu);

        // Show view mode toolbar if document has annotations (even in non-markup view)
        if (hasAnnotations && annotations.Count > 0)
        {
            container = container.WithView(BuildReactiveViewModeToolbar(host, node, rawContent, viewModeSubject, viewMode, annotations));
        }

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

        // Add Markdown children section if there are any
        if (markdownChildren.Count > 0)
        {
            container = container.WithView(BuildMarkdownChildrenSection(markdownChildren, nodePath));
        }

        return container;
    }

    /// <summary>
    /// Builds a section displaying child nodes grouped by Category (or NodeType as fallback).
    /// Uses MeshNodeThumbnailControl for consistent styling with the catalog.
    /// </summary>
    private static UiControl BuildMarkdownChildrenSection(IReadOnlyList<MeshNode> children, string _)
    {
        var section = Controls.Stack
            .WithWidth("100%")
            .WithStyle("margin-top: 32px; padding-top: 24px; border-top: 1px solid var(--neutral-stroke-rest);");

        // Group by Category if set, otherwise by NodeType
        var childGroups = children
            .Where(c => !string.IsNullOrEmpty(c.Category) || !string.IsNullOrEmpty(c.NodeType))
            .GroupBy(c => c.Category ?? c.NodeType!)
            .OrderBy(g => g.Key)
            .ToList();

        if (childGroups.Count == 0)
        {
            // Fallback for children without Category or NodeType
            var grid = Controls.LayoutGrid.WithSkin(s => s.WithSpacing(2));
            foreach (var child in children.OrderBy(c => c.DisplayOrder ?? int.MaxValue))
            {
                grid = grid.WithView(
                    MeshNodeThumbnailControl.FromNode(child, child.Path),
                    itemSkin => itemSkin.WithXs(12).WithSm(6).WithMd(4).WithLg(4));
            }
            section = section.WithView(grid);
            return section;
        }

        // Grid with grouped children
        var mainGrid = Controls.LayoutGrid.WithSkin(s => s.WithSpacing(2));

        foreach (var group in childGroups)
        {
            var displayName = MeshNodeLayoutAreas.GetGroupDisplayName(group.Key, group.Count());

            // Section header spans full width
            mainGrid = mainGrid.WithView(
                Controls.Html($"<h3 style=\"margin: 24px 0 8px 0;\">{displayName}</h3>"),
                itemSkin => itemSkin.WithXs(12));

            // Thumbnails in grid
            foreach (var child in group.OrderBy(c => c.DisplayOrder ?? int.MaxValue).ThenBy(c => c.Name))
            {
                mainGrid = mainGrid.WithView(
                    MeshNodeThumbnailControl.FromNode(child, child.Path),
                    itemSkin => itemSkin.WithXs(12).WithSm(6).WithMd(4).WithLg(4));
            }
        }

        section = section.WithView(mainGrid);
        return section;
    }

    /// <summary>
    /// Builds the split layout with content on the left and inline annotations on the right (Word Online-style).
    /// Annotations are positioned at the same height as their markers with connecting lines.
    /// </summary>
    private static UiControl BuildSplitLayoutWithAnnotations(
        LayoutAreaHost host,
        MeshNode? node,
        string content,
        List<ParsedAnnotation> annotations,
        ViewModeSubject viewModeSubject,
        AnnotationViewMode currentViewMode,
        AnnotationPanelState panelState,
        BehaviorSubject<AnnotationPanelState> panelStateSubject,
        IReadOnlyList<MeshNode> markdownChildren,
        IReadOnlyList<MeshNode> commentNodes)
    {
        var nodePath = node?.Path ?? host.Hub.Address.ToString();
        // Outer container - wider to accommodate larger annotation cards
        var outerContainer = Controls.Stack
            .WithWidth("100%")
            .WithStyle("max-width: 1400px; margin: 0 auto; padding: 24px; background: var(--neutral-layer-1); position: relative;");

        // Action menu positioned at top-right of content area
        var actionMenu = Controls.Stack
            .WithStyle("position: absolute; top: 24px; right: 24px; z-index: 10;")
            .WithView(BuildActionMenu(host, node));

        outerContainer = outerContainer.WithView(actionMenu);

        // Reactive view mode toolbar - pass node and raw content for persistence
        var rawContent = GetMarkdownContent(node);
        outerContainer = outerContainer.WithView(BuildReactiveViewModeToolbar(host, node, rawContent, viewModeSubject, currentViewMode, annotations));

        // Split layout using nested stacks with position:relative for SVG lines
        var splitLayout = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithWidth("100%")
            .WithStyle("gap: 24px; align-items: flex-start; position: relative;");

        // Left: Content area with markdown - takes remaining space
        var contentArea = Controls.Stack
            .WithClass("markdown-annotations-container")
            .WithStyle("flex: 1; min-width: 0; max-width: 750px; position: relative; line-height: 1.7; font-size: 1rem;")
            .WithView(new MarkdownControl(content));

        // Right: Annotations column
        // Comments come from MeshNode children; track change cards from ParsedAnnotation
        var trackChangeAnnotations = annotations.Where(a => a.Type != AnnotationType.Comment).ToList();
        var hasVisibleComments = commentNodes.Count > 0;

        var columnStyle = hasVisibleComments
            ? "flex: 0 0 340px; min-width: 300px; max-width: 380px; position: relative;"
            : "flex: 0 0 0; width: 0; min-width: 0; overflow: hidden; position: relative;";
        var annotationsColumn = Controls.Stack
            .WithClass("annotations-column")
            .WithStyle(columnStyle);

        // Separate top-level comments from replies
        var topLevelComments = commentNodes
            .Where(n => n.Content is Comment c && string.IsNullOrEmpty(c.ParentCommentId))
            .ToList();

        var repliesByParent = commentNodes
            .Where(n => n.Content is Comment c && !string.IsNullOrEmpty(c.ParentCommentId))
            .GroupBy(n => ((Comment)n.Content!).ParentCommentId!)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Visible comment cards built from top-level Comment MeshNodes
        foreach (var commentNode in topLevelComments)
        {
            var comment = commentNode.Content as Comment;
            if (comment == null) continue;

            repliesByParent.TryGetValue(comment.Id, out var replyNodes);

            annotationsColumn = annotationsColumn.WithView(
                BuildCommentNodeCard(host, node, rawContent, comment, commentNode.Path!, panelState, panelStateSubject,
                    replyNodes ?? new List<MeshNode>()));
        }

        // Hidden track change cards (Accept/Reject Blazor buttons as action targets for inline popover JS)
        foreach (var annotation in trackChangeAnnotations)
        {
            annotationsColumn = annotationsColumn.WithView(
                BuildAnnotationCard(host, node, rawContent, annotation, panelState, panelStateSubject, viewModeSubject));
        }

        // New comment form (hidden by default, shown by JS when text is selected)
        annotationsColumn = annotationsColumn.WithView(
            BuildNewCommentForm(host, node, rawContent, nodePath, panelStateSubject));

        splitLayout = splitLayout.WithView(contentArea);
        splitLayout = splitLayout.WithView(annotationsColumn);

        outerContainer = outerContainer.WithView(splitLayout);

        // Add JavaScript for positioning, connecting lines, and text selection
        outerContainer = outerContainer.WithView(BuildInlineAnnotationScript());

        // Add floating comment button for text selection
        outerContainer = outerContainer.WithView(Controls.Html(
            "<div class=\"floating-comment-btn\" style=\"display:none; position:absolute; z-index:1000;\">" +
            "<button class=\"add-comment-btn\" style=\"padding: 4px 12px; background: #3b82f6; color: white; border: none; border-radius: 4px; cursor: pointer; font-size: 0.85rem; box-shadow: 0 2px 6px rgba(0,0,0,0.2);\">Comment</button>" +
            "</div>"));

        // Add Markdown children section if there are any
        if (markdownChildren.Count > 0)
        {
            outerContainer = outerContainer.WithView(BuildMarkdownChildrenSection(markdownChildren, nodePath));
        }

        return outerContainer;
    }

    /// <summary>
    /// Builds a comment card from a Comment MeshNode (not a ParsedAnnotation).
    /// Shows author, highlighted text, comment text, replies, and Reply/Resolve buttons.
    /// </summary>
    private static UiControl BuildCommentNodeCard(
        LayoutAreaHost host,
        MeshNode? node,
        string rawContent,
        Comment comment,
        string commentPath,
        AnnotationPanelState panelState,
        BehaviorSubject<AnnotationPanelState> panelStateSubject,
        IReadOnlyList<MeshNode> replyNodes)
    {
        var typeColor = "#3b82f6";
        var author = comment.Author ?? "Unknown";
        var authorInitial = author.Length > 0 ? author[0].ToString().ToUpper() : "?";
        var markerId = comment.MarkerId ?? comment.Id;

        // Card container
        var card = Controls.Stack
            .WithStyle($"padding: 12px; background: var(--neutral-layer-1); border-radius: 6px; border-left: 4px solid {typeColor}; margin-bottom: 8px; box-shadow: 0 1px 3px rgba(0,0,0,0.12);");

        // Header row: Avatar + Author + Type badge
        var headerHtml = $@"
            <div style=""display: flex; align-items: center; gap: 8px; margin-bottom: 8px;"">
                <div style=""width: 32px; height: 32px; border-radius: 50%; background: {typeColor}; color: white;
                            display: flex; align-items: center; justify-content: center; font-weight: 600; font-size: 14px;"">
                    {System.Web.HttpUtility.HtmlEncode(authorInitial)}
                </div>
                <div style=""flex: 1;"">
                    <div style=""font-weight: 600; font-size: 0.9rem; color: var(--neutral-foreground-rest);"">{System.Web.HttpUtility.HtmlEncode(author)}</div>
                    <div style=""font-size: 0.75rem; color: var(--neutral-foreground-hint);"">{Graph.CommentsView.FormatTimeAgo(comment.CreatedAt)}</div>
                </div>
            </div>";
        card = card.WithView(Controls.Html(headerHtml));

        // Highlighted text quote
        if (!string.IsNullOrEmpty(comment.HighlightedText))
        {
            card = card.WithView(Controls.Html($@"
                <div style=""font-size: 0.85rem; color: var(--neutral-foreground-rest); padding: 8px 10px;
                            background: rgba(59, 130, 246, 0.1); border-radius: 4px; margin-bottom: 8px;
                            border-left: 2px solid {typeColor}; font-style: italic;"">
                    ""{System.Web.HttpUtility.HtmlEncode(comment.HighlightedText)}""
                </div>"));
        }

        // Comment text
        if (!string.IsNullOrEmpty(comment.Text))
        {
            card = card.WithView(Controls.Html($@"
                <div style=""font-size: 0.9rem; color: var(--neutral-foreground-rest); line-height: 1.5; margin-bottom: 8px;"">
                    {System.Web.HttpUtility.HtmlEncode(comment.Text)}
                </div>"));
        }

        // Render reply MeshNodes as Thumbnail or inline edit form
        foreach (var replyNode in replyNodes.OrderBy(r => ((Comment)r.Content!).CreatedAt))
        {
            if (replyNode.Path == panelState.EditingReplyPath)
            {
                // Show inline edit form for this reply
                card = card.WithView(BuildReplyEditForm(host, replyNode, panelState, panelStateSubject));
            }
            else
            {
                // Show as CommentNodeType Thumbnail
                card = card.WithView(CommentLayoutAreas.BuildThumbnail(replyNode));
            }
        }

        // Action buttons — Reply creates a MeshNode immediately, Resolve removes marker + deletes node
        var buttonRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 8px; margin-top: 8px;");

        buttonRow = buttonRow.WithView(
            Controls.Button("Reply")
                .WithAppearance(Appearance.Neutral)
                .WithStyle("padding: 6px 16px; font-size: 0.85rem;")
                .WithClickAction(async _ =>
                {
                    var replyId = Guid.NewGuid().AsString();
                    var replyComment = new Comment
                    {
                        Id = replyId,
                        NodePath = commentPath,
                        Author = "",
                        Text = "",
                        ParentCommentId = comment.Id,
                        Status = CommentStatus.Active
                    };
                    var replyNode = new MeshNode($"{commentPath}/{replyId}")
                    {
                        Name = "Reply",
                        NodeType = CommentNodeType.NodeType,
                        Content = replyComment
                    };

                    var meshCatalog = host.Hub.ServiceProvider.GetRequiredService<IMeshCatalog>();
                    await meshCatalog.CreateNodeAsync(replyNode);

                    panelStateSubject.OnNext(panelState with
                    {
                        EditingReplyPath = replyNode.Path
                    });
                }));

        buttonRow = buttonRow.WithView(
            Controls.Button("Resolve")
                .WithAppearance(Appearance.Stealth)
                .WithStyle("padding: 6px 16px; font-size: 0.85rem;")
                .WithClickAction(async _ =>
                {
                    if (node == null) return;
                    // Remove marker from markdown
                    var newContent = AnnotationMarkdownExtension.ResolveComment(rawContent, markerId);
                    var updatedNode = node with { Content = new MarkdownContent { Content = newContent } };
                    host.Hub.Post(
                        new DataChangeRequest().WithUpdates(updatedNode),
                        o => o.WithTarget(host.Hub.Address));

                    // Delete the Comment MeshNode
                    var meshCatalog = host.Hub.ServiceProvider.GetRequiredService<IMeshCatalog>();
                    await meshCatalog.DeleteNodeAsync(commentPath, recursive: true);
                }));

        card = card.WithView(buttonRow);

        return card
            .WithClass($"annotation-card annotation-for-{markerId} annotation-type-comment");
    }

    /// <summary>
    /// Builds an inline edit form for a reply MeshNode using the property overview (auto-save).
    /// Shows Author + Text fields (other Comment properties hidden via [Browsable(false)]).
    /// Done closes the form; Cancel deletes the reply node.
    /// </summary>
    private static UiControl BuildReplyEditForm(
        LayoutAreaHost host,
        MeshNode replyNode,
        AnnotationPanelState panelState,
        BehaviorSubject<AnnotationPanelState> panelStateSubject)
    {
        var form = Controls.Stack
            .WithStyle("margin: 4px 0; padding: 12px; background: var(--neutral-layer-2); border-radius: 6px; border-left: 3px solid var(--accent-fill-rest);");

        // Property editor with auto-save
        form = form.WithView(OverviewLayoutArea.BuildPropertyOverview(host, replyNode));

        // Done/Cancel buttons
        var buttonRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 8px; justify-content: flex-end; margin-top: 8px;");

        buttonRow = buttonRow.WithView(
            Controls.Button("Cancel")
                .WithAppearance(Appearance.Stealth)
                .WithStyle("padding: 6px 16px; font-size: 0.85rem;")
                .WithClickAction(async _ =>
                {
                    // Delete the empty reply node
                    var meshCatalog = host.Hub.ServiceProvider.GetRequiredService<IMeshCatalog>();
                    await meshCatalog.DeleteNodeAsync(replyNode.Path!, recursive: false);
                    panelStateSubject.OnNext(panelState with { EditingReplyPath = null });
                }));

        buttonRow = buttonRow.WithView(
            Controls.Button("Done")
                .WithAppearance(Appearance.Accent)
                .WithStyle("padding: 6px 16px; font-size: 0.85rem;")
                .WithClickAction(_ =>
                {
                    // Auto-save already persisted; just close the edit form
                    panelStateSubject.OnNext(panelState with { EditingReplyPath = null });
                }));

        form = form.WithView(buttonRow);
        return form;
    }

    /// <summary>
    /// Builds a reply form for a comment that creates a child Comment MeshNode.
    /// </summary>
    private static UiControl BuildCommentReplyForm(
        LayoutAreaHost host,
        string commentId,
        string commentPath,
        AnnotationPanelState panelState,
        BehaviorSubject<AnnotationPanelState> panelStateSubject)
    {
        var replyDataId = $"Reply_{commentId}";

        var form = Controls.Stack
            .WithStyle("margin-top: 12px; padding: 12px; background: var(--neutral-layer-2); border-radius: 6px;");

        var editControl = host.Edit(new ReplyFormModel(), replyDataId);
        if (editControl != null)
        {
            var editWrapper = Controls.Stack
                .WithStyle("margin-bottom: 12px;")
                .WithView(editControl);
            form = form.WithView(editWrapper);
        }

        var buttonRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 8px; justify-content: flex-end;");

        buttonRow = buttonRow.WithView(
            Controls.Button("Cancel")
                .WithAppearance(Appearance.Stealth)
                .WithStyle("padding: 6px 16px; font-size: 0.85rem;")
                .WithClickAction(_ => panelStateSubject.OnNext(
                    panelState with { ReplyingToAnnotationId = null })));

        buttonRow = buttonRow.WithView(
            Controls.Button("Submit")
                .WithAppearance(Appearance.Accent)
                .WithStyle("padding: 6px 16px; font-size: 0.85rem;")
                .WithClickAction(async ctx =>
                {
                    var replyModel = await ctx.Host.Stream.GetDataAsync<ReplyFormModel>(replyDataId);
                    if (replyModel != null && !string.IsNullOrWhiteSpace(replyModel.Text))
                    {
                        // Create a child Comment MeshNode as a reply
                        var replyId = Guid.NewGuid().AsString();
                        var replyComment = new Comment
                        {
                            Id = replyId,
                            NodePath = commentPath,
                            Author = "User",
                            Text = replyModel.Text,
                            ParentCommentId = commentId,
                            Status = CommentStatus.Active
                        };
                        var replyNode = new MeshNode($"{commentPath}/{replyId}")
                        {
                            Name = $"Reply by User",
                            NodeType = CommentNodeType.NodeType,
                            Content = replyComment
                        };

                        var meshCatalog = host.Hub.ServiceProvider.GetRequiredService<IMeshCatalog>();
                        await meshCatalog.CreateNodeAsync(replyNode);

                        // Just close the form — reactive subscription handles display
                        panelStateSubject.OnNext(panelState with { ReplyingToAnnotationId = null });
                    }
                }));

        form = form.WithView(buttonRow);
        return form;
    }

    /// <summary>
    /// Builds the new comment form that appears in the annotations column when the user selects text.
    /// Hidden by default; shown by JS when text is selected and the floating comment button is clicked.
    /// </summary>
    private static UiControl BuildNewCommentForm(
        LayoutAreaHost host,
        MeshNode? node,
        string rawContent,
        string nodePath,
        BehaviorSubject<AnnotationPanelState> panelStateSubject)
    {
        var commentFormDataId = $"NewComment_{nodePath.Replace("/", "-")}";

        var form = Controls.Stack
            .WithClass("new-comment-form")
            .WithStyle("display: none; padding: 12px; background: var(--neutral-layer-1); border-radius: 6px; border-left: 4px solid #3b82f6; margin-bottom: 12px; box-shadow: 0 1px 3px rgba(0,0,0,0.12);");

        // Header
        form = form.WithView(Controls.Html(
            "<div style=\"font-weight: 600; font-size: 0.9rem; margin-bottom: 8px; color: #3b82f6;\">New Comment</div>"));

        // Selected text display (filled by JS)
        form = form.WithView(Controls.Html(
            "<div class=\"new-comment-selected-text\" style=\"font-size: 0.85rem; padding: 8px 10px; background: rgba(59, 130, 246, 0.1); border-radius: 4px; margin-bottom: 8px; border-left: 2px solid #3b82f6; font-style: italic; display: none;\"></div>"));

        // Comment text area using host.Edit
        var editControl = host.Edit(new CommentFormModel(), commentFormDataId);
        if (editControl != null)
        {
            var editWrapper = Controls.Stack
                .WithStyle("margin-bottom: 12px;")
                .WithView(editControl);
            form = form.WithView(editWrapper);
        }

        // Button row
        var buttonRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 8px; justify-content: flex-end;");

        buttonRow = buttonRow.WithView(
            Controls.Button("Cancel")
                .WithAppearance(Appearance.Stealth)
                .WithStyle("padding: 6px 16px; font-size: 0.85rem;")
                .WithClass("new-comment-cancel")
                .WithClickAction(_ =>
                {
                    // JS will hide the form
                }));

        buttonRow = buttonRow.WithView(
            Controls.Button("Submit")
                .WithAppearance(Appearance.Accent)
                .WithStyle("padding: 6px 16px; font-size: 0.85rem;")
                .WithClass("new-comment-submit")
                .WithClickAction(async ctx =>
                {
                    if (node == null) return;

                    var formModel = await ctx.Host.Stream.GetDataAsync<CommentFormModel>(commentFormDataId);
                    var selectedText = formModel?.SelectedText ?? "";
                    var commentText = formModel?.CommentText ?? "";

                    if (string.IsNullOrWhiteSpace(commentText)) return;

                    var markerId = Guid.NewGuid().AsString();
                    var commentId = Guid.NewGuid().AsString();
                    var author = "User";

                    // 1. Create Comment MeshNode
                    var comment = new Comment
                    {
                        Id = commentId,
                        NodePath = nodePath,
                        MarkerId = markerId,
                        HighlightedText = selectedText,
                        Author = author,
                        Text = commentText,
                        Status = CommentStatus.Active
                    };
                    var commentNode = new MeshNode($"{nodePath}/{commentId}")
                    {
                        Name = $"Comment by {author}",
                        NodeType = CommentNodeType.NodeType,
                        Content = comment
                    };

                    var meshCatalog = host.Hub.ServiceProvider.GetRequiredService<IMeshCatalog>();
                    await meshCatalog.CreateNodeAsync(commentNode);

                    // 2. Insert marker into markdown (if text was selected)
                    if (!string.IsNullOrEmpty(selectedText))
                    {
                        var marker = $"<!--comment:{markerId}-->";
                        var closing = $"<!--/comment:{markerId}-->";
                        var idx = rawContent.IndexOf(selectedText, StringComparison.Ordinal);
                        if (idx >= 0)
                        {
                            var newContent = rawContent.Insert(idx + selectedText.Length, closing)
                                                       .Insert(idx, marker);
                            var updatedNode = node with { Content = new MarkdownContent { Content = newContent } };
                            host.Hub.Post(
                                new DataChangeRequest().WithUpdates(updatedNode),
                                o => o.WithTarget(host.Hub.Address));
                        }
                    }
                }));

        form = form.WithView(buttonRow);
        return form;
    }

    /// <summary>
    /// Builds an annotation card in Word Online style - always expanded, readable.
    /// Accept/Reject buttons persist changes via DataChangeRequest.
    /// </summary>
    private static UiControl BuildAnnotationCard(
        LayoutAreaHost host,
        MeshNode? node,
        string rawContent,
        ParsedAnnotation annotation,
        AnnotationPanelState panelState,
        BehaviorSubject<AnnotationPanelState> panelStateSubject,
        ViewModeSubject viewModeSubject)
    {
        var typeColor = annotation.Type switch
        {
            AnnotationType.Comment => "#3b82f6",
            AnnotationType.Insert => "#22c55e",
            AnnotationType.Delete => "#ef4444",
            _ => "#6b7280"
        };

        var typeLabel = annotation.Type switch
        {
            AnnotationType.Comment => "Comment",
            AnnotationType.Insert => "Inserted",
            AnnotationType.Delete => "Deleted",
            _ => "Change"
        };

        var author = annotation.Author ?? "Unknown";
        var authorInitial = author.Length > 0 ? author[0].ToString().ToUpper() : "?";
        var isReplyingToThis = panelState.ReplyingToAnnotationId == annotation.Id;

        // Card container - Word Online style with proper sizing
        var card = Controls.Stack
            .WithStyle($"padding: 12px; background: var(--neutral-layer-1); border-radius: 6px; border-left: 4px solid {typeColor}; margin-bottom: 8px; box-shadow: 0 1px 3px rgba(0,0,0,0.12);");

        // Header row: Avatar + Author + Type badge
        var headerHtml = $@"
            <div style=""display: flex; align-items: center; gap: 8px; margin-bottom: 8px;"">
                <div style=""width: 32px; height: 32px; border-radius: 50%; background: {typeColor}; color: white;
                            display: flex; align-items: center; justify-content: center; font-weight: 600; font-size: 14px;"">
                    {System.Web.HttpUtility.HtmlEncode(authorInitial)}
                </div>
                <div style=""flex: 1;"">
                    <div style=""font-weight: 600; font-size: 0.9rem; color: var(--neutral-foreground-rest);"">{System.Web.HttpUtility.HtmlEncode(author)}</div>
                    <div style=""font-size: 0.75rem; color: var(--neutral-foreground-hint);"">{typeLabel}</div>
                </div>
            </div>";
        card = card.WithView(Controls.Html(headerHtml));

        // Quoted/highlighted text from document
        var highlightedText = annotation.HighlightedText;
        if (!string.IsNullOrEmpty(highlightedText))
        {
            var bgColor = annotation.Type switch
            {
                AnnotationType.Insert => "rgba(34, 197, 94, 0.15)",
                AnnotationType.Delete => "rgba(239, 68, 68, 0.15)",
                _ => "rgba(59, 130, 246, 0.1)"
            };
            var textDecoration = annotation.Type == AnnotationType.Delete ? "text-decoration: line-through;" : "";

            card = card.WithView(Controls.Html($@"
                <div style=""font-size: 0.85rem; color: var(--neutral-foreground-rest); padding: 8px 10px;
                            background: {bgColor}; border-radius: 4px; margin-bottom: 8px;
                            border-left: 2px solid {typeColor}; font-style: italic; {textDecoration}"">
                    ""{System.Web.HttpUtility.HtmlEncode(highlightedText)}""
                </div>"));
        }

        // Comment text (for comments)
        if (annotation.Type == AnnotationType.Comment && !string.IsNullOrEmpty(annotation.CommentText))
        {
            card = card.WithView(Controls.Html($@"
                <div style=""font-size: 0.9rem; color: var(--neutral-foreground-rest); line-height: 1.5; margin-bottom: 8px;"">
                    {System.Web.HttpUtility.HtmlEncode(annotation.CommentText)}
                </div>"));
        }

        // Action buttons - always visible
        if (annotation.Type == AnnotationType.Comment)
        {
            if (isReplyingToThis)
            {
                card = card.WithView(BuildReplyForm(host, annotation.Id, panelState, panelStateSubject));
            }
            else
            {
                var buttonRow = Controls.Stack
                    .WithOrientation(Orientation.Horizontal)
                    .WithStyle("gap: 8px; margin-top: 8px;");

                buttonRow = buttonRow.WithView(
                    Controls.Button("Reply")
                        .WithAppearance(Appearance.Neutral)
                        .WithStyle("padding: 6px 16px; font-size: 0.85rem;")
                        .WithClickAction(_ => panelStateSubject.OnNext(
                            panelState with { ReplyingToAnnotationId = annotation.Id })));

                buttonRow = buttonRow.WithView(
                    Controls.Button("Resolve")
                        .WithAppearance(Appearance.Stealth)
                        .WithStyle("padding: 6px 16px; font-size: 0.85rem;")
                        .WithClickAction(_ =>
                        {
                            if (node == null) return;
                            var newContent = AnnotationMarkdownExtension.ResolveComment(rawContent, annotation.Id);
                            var updatedNode = node with { Content = new MarkdownContent { Content = newContent } };
                            host.Hub.Post(
                                new DataChangeRequest().WithUpdates(updatedNode),
                                o => o.WithTarget(host.Hub.Address));
                        }));

                card = card.WithView(buttonRow);
            }
        }
        else
        {
            // Track changes - Accept/Reject buttons
            var buttonRow = Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("gap: 8px; margin-top: 8px;");

            buttonRow = buttonRow.WithView(
                Controls.Button("Accept")
                    .WithStyle("padding: 6px 16px; font-size: 0.85rem; background: #22c55e; color: white; border: none; border-radius: 4px;")
                    .WithClickAction(_ =>
                    {
                        if (node == null) return;
                        var newContent = AnnotationMarkdownExtension.AcceptChange(rawContent, annotation.Id);
                        var updatedNode = node with { Content = new MarkdownContent { Content = newContent } };
                        host.Hub.Post(
                            new DataChangeRequest().WithUpdates(updatedNode),
                            o => o.WithTarget(host.Hub.Address));
                    }));

            buttonRow = buttonRow.WithView(
                Controls.Button("Reject")
                    .WithStyle("padding: 6px 16px; font-size: 0.85rem; background: #ef4444; color: white; border: none; border-radius: 4px;")
                    .WithClickAction(_ =>
                    {
                        if (node == null) return;
                        var newContent = AnnotationMarkdownExtension.RejectChange(rawContent, annotation.Id);
                        var updatedNode = node with { Content = new MarkdownContent { Content = newContent } };
                        host.Hub.Post(
                            new DataChangeRequest().WithUpdates(updatedNode),
                            o => o.WithTarget(host.Hub.Address));
                    }));

            card = card.WithView(buttonRow);
        }

        // Use WithClass for identification (class-based selectors work reliably in Blazor)
        return card
            .WithClass($"annotation-card annotation-for-{annotation.Id} annotation-type-{annotation.Type.ToString().ToLowerInvariant()}");
    }

    /// <summary>
    /// Builds the inline reply form for a comment using host.Edit().
    /// </summary>
    private static UiControl BuildReplyForm(
        LayoutAreaHost host,
        string annotationId,
        AnnotationPanelState panelState,
        BehaviorSubject<AnnotationPanelState> panelStateSubject)
    {
        var replyDataId = $"Reply_{annotationId}";

        var form = Controls.Stack
            .WithStyle("margin-top: 12px; padding: 12px; background: var(--neutral-layer-2); border-radius: 6px;");

        // Reply textarea using host.Edit with ReplyFormModel
        var editControl = host.Edit(new ReplyFormModel(), replyDataId);
        if (editControl != null)
        {
            var editWrapper = Controls.Stack
                .WithStyle("margin-bottom: 12px;")
                .WithView(editControl);
            form = form.WithView(editWrapper);
        }

        // Button row
        var buttonRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 8px; justify-content: flex-end;");

        buttonRow = buttonRow.WithView(
            Controls.Button("Cancel")
                .WithAppearance(Appearance.Stealth)
                .WithStyle("padding: 6px 16px; font-size: 0.85rem;")
                .WithClickAction(_ => panelStateSubject.OnNext(
                    panelState with { ReplyingToAnnotationId = null })));

        buttonRow = buttonRow.WithView(
            Controls.Button("Submit")
                .WithAppearance(Appearance.Accent)
                .WithStyle("padding: 6px 16px; font-size: 0.85rem;")
                .WithClickAction(async ctx =>
                {
                    // Get the reply text from the form data
                    var replyModel = await ctx.Host.Stream.GetDataAsync<ReplyFormModel>(replyDataId);
                    if (replyModel != null && !string.IsNullOrWhiteSpace(replyModel.Text))
                    {
                        // Close the form — track change replies are informational only
                        panelStateSubject.OnNext(panelState with { ReplyingToAnnotationId = null });
                    }
                }));

        form = form.WithView(buttonRow);
        return form;
    }

    /// <summary>
    /// Builds JavaScript for positioning annotation cards and drawing SVG connecting lines.
    /// Uses class-based selectors (not IDs) since WithClass renders to HTML but WithId does not in Blazor.
    /// </summary>
    private static UiControl BuildInlineAnnotationScript()
    {
        return Controls.Html(@"
<script>
(function() {
    var minCardGap = 12;

    // Extract annotation ID from class list (e.g., 'annotation-for-c1' → 'c1')
    function getAnnotationIdFromCard(card) {
        var cls = Array.from(card.classList).find(function(c) { return c.startsWith('annotation-for-'); });
        return cls ? cls.replace('annotation-for-', '') : null;
    }

    // Get annotation type color from card classes
    function getAnnotationColor(card) {
        if (card.classList.contains('annotation-type-insert')) return '#22c55e';
        if (card.classList.contains('annotation-type-delete')) return '#ef4444';
        if (card.classList.contains('annotation-type-comment')) return '#3b82f6';
        return '#6b7280';
    }

    function drawConnectingLines() {
        var container = document.querySelector('.markdown-annotations-container');
        var annotationsCol = document.querySelector('.annotations-column');
        if (!container || !annotationsCol) return;

        // Remove old SVG if exists
        var oldSvg = document.querySelector('.annotation-connecting-lines');
        if (oldSvg) oldSvg.remove();

        // Create SVG in the split layout parent
        var splitLayout = annotationsCol.parentElement;
        if (!splitLayout) return;

        var svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
        svg.classList.add('annotation-connecting-lines');
        var svgHeight = Math.max(splitLayout.scrollHeight, container.scrollHeight, annotationsCol.scrollHeight);
        svg.style.cssText = 'position:absolute;top:0;left:0;pointer-events:none;z-index:1;overflow:visible;';
        svg.setAttribute('width', splitLayout.scrollWidth);
        svg.setAttribute('height', svgHeight);
        splitLayout.insertBefore(svg, splitLayout.firstChild);

        var splitRect = splitLayout.getBoundingClientRect();

        // Only draw lines for comment cards (track change cards are hidden)
        var cards = annotationsCol.querySelectorAll('.annotation-card.annotation-type-comment');
        cards.forEach(function(card) {
            var annotationId = getAnnotationIdFromCard(card);
            if (!annotationId) return;
            var color = getAnnotationColor(card);
            var marker = container.querySelector('[data-comment-id=""' + annotationId + '""]');

            if (marker && card.offsetParent) {
                var markerRect = marker.getBoundingClientRect();
                var cardRect = card.getBoundingClientRect();

                var markerX = markerRect.right - splitRect.left + splitLayout.scrollLeft + 4;
                var markerY = markerRect.top + markerRect.height / 2 - splitRect.top + splitLayout.scrollTop;
                var cardX = cardRect.left - splitRect.left + splitLayout.scrollLeft - 4;
                var cardY = cardRect.top + Math.min(24, cardRect.height / 2) - splitRect.top + splitLayout.scrollTop;

                var path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
                var midX = (markerX + cardX) / 2;
                path.setAttribute('d', 'M ' + markerX + ' ' + markerY + ' Q ' + midX + ' ' + markerY + ' ' + midX + ' ' + ((markerY + cardY) / 2) + ' Q ' + midX + ' ' + cardY + ' ' + cardX + ' ' + cardY);
                path.setAttribute('stroke', color);
                path.setAttribute('stroke-width', '1.5');
                path.setAttribute('fill', 'none');
                path.setAttribute('opacity', '0.5');
                path.setAttribute('data-annotation', annotationId);
                svg.appendChild(path);

                var circle = document.createElementNS('http://www.w3.org/2000/svg', 'circle');
                circle.setAttribute('cx', markerX);
                circle.setAttribute('cy', markerY);
                circle.setAttribute('r', '3');
                circle.setAttribute('fill', color);
                circle.setAttribute('opacity', '0.7');
                svg.appendChild(circle);
            }
        });
    }

    function positionAnnotationCards() {
        var container = document.querySelector('.markdown-annotations-container');
        var annotationsCol = document.querySelector('.annotations-column');
        if (!container || !annotationsCol) return;

        // Only position comment cards (track change cards are hidden)
        var cards = annotationsCol.querySelectorAll('.annotation-card.annotation-type-comment');
        var positions = [];

        cards.forEach(function(card) {
            var annotationId = getAnnotationIdFromCard(card);
            if (!annotationId) return;

            var marker = container.querySelector('[data-comment-id=""' + annotationId + '""]');

            if (marker) {
                var markerRect = marker.getBoundingClientRect();
                var colRect = annotationsCol.getBoundingClientRect();
                var desiredTop = markerRect.top - colRect.top + annotationsCol.scrollTop;

                positions.push({
                    card: card,
                    annotationId: annotationId,
                    desiredTop: desiredTop,
                    height: card.offsetHeight || 80
                });
            }
        });

        positions.sort(function(a, b) { return a.desiredTop - b.desiredTop; });

        var lastBottom = 0;
        positions.forEach(function(pos) {
            var actualTop = Math.max(pos.desiredTop, lastBottom + minCardGap);
            pos.actualTop = actualTop;
            lastBottom = actualTop + pos.height;
        });

        positions.forEach(function(pos) {
            pos.card.style.position = 'absolute';
            pos.card.style.top = pos.actualTop + 'px';
            pos.card.style.left = '0';
            pos.card.style.right = '0';
        });

        if (positions.length > 0) {
            var lastPos = positions[positions.length - 1];
            annotationsCol.style.minHeight = (lastPos.actualTop + lastPos.height + 16) + 'px';
        }

        requestAnimationFrame(drawConnectingLines);
    }

    function waitForMarkersAndPosition() {
        var container = document.querySelector('.markdown-annotations-container');
        var annotationsCol = document.querySelector('.annotations-column');
        if (!container || !annotationsCol) {
            requestAnimationFrame(waitForMarkersAndPosition);
            return;
        }

        var markers = container.querySelectorAll('[data-comment-id], [data-change-id]');
        var cards = annotationsCol.querySelectorAll('.annotation-card');
        if (markers.length === 0 || cards.length === 0) {
            requestAnimationFrame(waitForMarkersAndPosition);
            return;
        }

        positionAnnotationCards();
        requestAnimationFrame(function() {
            requestAnimationFrame(positionAnnotationCards);
        });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', waitForMarkersAndPosition);
    } else {
        requestAnimationFrame(waitForMarkersAndPosition);
    }

    window.addEventListener('resize', function() {
        requestAnimationFrame(positionAnnotationCards);
    });

    var observer = new MutationObserver(function() {
        requestAnimationFrame(positionAnnotationCards);
    });

    function setupObservers() {
        var container = document.querySelector('.markdown-annotations-container');
        var annotationsCol = document.querySelector('.annotations-column');
        if (annotationsCol) {
            observer.observe(annotationsCol, { childList: true, subtree: true, attributes: true, characterData: true });
        }
        if (container) {
            observer.observe(container, { childList: true, subtree: true });
        }
    }
    requestAnimationFrame(setupObservers);

    // Click handler for track change inline popovers and comment highlights
    document.addEventListener('click', function(e) {
        // Handle Accept/Reject buttons inside inline popovers
        var actionBtn = e.target.closest('.annotation-actions button');
        if (actionBtn) {
            var changeId = actionBtn.dataset.changeId;
            var action = actionBtn.dataset.action;
            if (changeId && action) {
                // Find the matching card in the side panel and click the corresponding button
                var panelCard = document.querySelector('.annotation-for-' + changeId);
                if (panelCard) {
                    panelCard.scrollIntoView({ behavior: 'smooth', block: 'center' });
                    panelCard.classList.add('active');
                    // Find and click the Accept or Reject button in the panel card
                    var btnText = action === 'accept' ? 'Accept' : 'Reject';
                    var panelButtons = panelCard.querySelectorAll('fluent-button, button');
                    panelButtons.forEach(function(btn) {
                        if (btn.textContent.trim() === btnText) {
                            btn.click();
                        }
                    });
                }
            }
            e.stopPropagation();
            return;
        }

        // Close all open track change popovers first
        document.querySelectorAll('.show-label').forEach(function(el) {
            el.classList.remove('show-label');
        });

        // Toggle popover on track change spans
        var trackSpan = e.target.closest('.track-insert, .track-delete');
        if (trackSpan) {
            trackSpan.classList.add('show-label');
            e.stopPropagation();
            return;
        }

        // Highlight on comment click → scroll to card in side panel
        var commentMarker = e.target.closest('[data-comment-id]');
        if (commentMarker) {
            var id = commentMarker.dataset.commentId;
            highlightAnnotation(id);
            return;
        }
    });

    // Click on annotation card → highlight the corresponding text
    document.addEventListener('click', function(e) {
        var card = e.target.closest('.annotation-card');
        if (card) {
            var annotationId = getAnnotationIdFromCard(card);
            if (annotationId) {
                highlightAnnotation(annotationId);
            }
        }
    });

    window.highlightAnnotation = function(annotationId) {
        var container = document.querySelector('.markdown-annotations-container');
        if (!container) return;

        // Remove previous highlights
        document.querySelectorAll('.annotation-active').forEach(function(el) {
            el.classList.remove('annotation-active');
        });
        document.querySelectorAll('.annotation-card.active').forEach(function(el) {
            el.classList.remove('active');
        });

        // Highlight marker in content
        var marker = container.querySelector('[data-comment-id=""' + annotationId + '""]') ||
                     container.querySelector('[data-change-id=""' + annotationId + '""]');
        if (marker) {
            marker.classList.add('annotation-active');
        }

        // Highlight card in side panel
        var card = document.querySelector('.annotation-for-' + annotationId);
        if (card) {
            card.classList.add('active');
            card.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
        }

        // Highlight the SVG line
        var svg = document.querySelector('.annotation-connecting-lines');
        if (svg) {
            svg.querySelectorAll('path').forEach(function(p) {
                if (p.dataset.annotation === annotationId) {
                    p.setAttribute('opacity', '0.8');
                    p.setAttribute('stroke-width', '2');
                } else {
                    p.setAttribute('opacity', '0.3');
                    p.setAttribute('stroke-width', '1');
                }
            });
        }
    };

    // Text selection → floating comment button
    document.addEventListener('mouseup', function(e) {
        var sel = window.getSelection();
        var text = sel ? sel.toString().trim() : '';
        var container = document.querySelector('.markdown-annotations-container');
        var floatingBtn = document.querySelector('.floating-comment-btn');
        if (!floatingBtn) return;

        if (text.length > 0 && container && container.contains(sel.anchorNode)) {
            var range = sel.getRangeAt(0);
            var rect = range.getBoundingClientRect();
            floatingBtn.style.display = 'block';
            floatingBtn.style.top = (rect.top + window.scrollY - 36) + 'px';
            floatingBtn.style.left = (rect.left + window.scrollX) + 'px';
            window.__pendingSelection = text;
        } else if (!e.target.closest('.floating-comment-btn, .new-comment-form')) {
            floatingBtn.style.display = 'none';
        }
    });

    // Floating comment button click → show new comment form
    document.addEventListener('click', function(e) {
        var addBtn = e.target.closest('.add-comment-btn');
        if (!addBtn) return;

        var floatingBtn = document.querySelector('.floating-comment-btn');
        if (floatingBtn) floatingBtn.style.display = 'none';

        var form = document.querySelector('.new-comment-form');
        if (form) {
            form.style.display = 'block';
            // Display selected text in the form
            var selectedTextEl = form.querySelector('.new-comment-selected-text');
            if (selectedTextEl && window.__pendingSelection) {
                selectedTextEl.textContent = '""' + window.__pendingSelection + '""';
                selectedTextEl.style.display = 'block';
            }
            // Try to set the selected text in the form data
            var textarea = form.querySelector('textarea, fluent-text-area');
            if (textarea) textarea.focus();
        }
    });

    // Cancel button in new comment form
    document.addEventListener('click', function(e) {
        if (e.target.closest('.new-comment-cancel')) {
            var form = document.querySelector('.new-comment-form');
            if (form) form.style.display = 'none';
            window.__pendingSelection = null;
        }
    });

    // After submit, hide form
    document.addEventListener('click', function(e) {
        if (e.target.closest('.new-comment-submit')) {
            setTimeout(function() {
                var form = document.querySelector('.new-comment-form');
                if (form) form.style.display = 'none';
                window.__pendingSelection = null;
            }, 500);
        }
    });

})();
</script>
<style>
.annotations-column {
    position: relative;
    display: flex;
    flex-direction: column;
    gap: 8px;
}
.annotation-card {
    transition: all 0.2s ease;
}
.annotation-card:hover {
    transform: translateX(-2px);
    box-shadow: 0 2px 8px rgba(0,0,0,0.15) !important;
    z-index: 10;
}
.annotation-card.active {
    box-shadow: 0 0 0 2px var(--accent-fill-rest), 0 2px 8px rgba(0,0,0,0.18) !important;
    z-index: 10;
}
.annotation-active {
    background: rgba(59, 130, 246, 0.15) !important;
    box-shadow: 0 0 0 2px var(--accent-fill-rest);
    border-radius: 3px;
}
/* Track changes: visible inline with highlighting, click to show popover */
.track-insert, .track-delete {
    cursor: pointer;
    position: relative;
}
/* Comment highlights remain visible */
.comment-highlight {
    cursor: pointer;
}
/* Hide track change cards in side panel (they exist only as Blazor action targets) */
.annotation-type-insert,
.annotation-type-delete {
    display: none !important;
}
</style>
        ");
    }

    /// <summary>
    /// Builds a reactive view mode toolbar that re-renders the markdown on mode change.
    /// Accept All and Reject All persist changes via DataChangeRequest.
    /// </summary>
    private static UiControl BuildReactiveViewModeToolbar(
        LayoutAreaHost host,
        MeshNode? node,
        string rawContent,
        ViewModeSubject viewModeSubject,
        AnnotationViewMode currentMode,
        List<ParsedAnnotation> annotations)
    {
        var trackChanges = annotations.Where(a => a.Type != AnnotationType.Comment).ToList();

        var toolbar = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithWidth("100%")
            .WithStyle("align-items: center; gap: 8px; margin-bottom: 16px; padding: 8px 12px; background: var(--neutral-layer-2); border-radius: 6px; flex-wrap: wrap;");

        // Label
        toolbar = toolbar.WithView(
            Controls.Html("<span style=\"font-size: 0.85rem; color: var(--neutral-foreground-hint); margin-right: 8px;\">View:</span>"));

        // View mode buttons using Controls.Button with click actions
        toolbar = toolbar.WithView(CreateViewModeButton("Markup", AnnotationViewMode.Markup, currentMode, viewModeSubject));
        toolbar = toolbar.WithView(CreateViewModeButton("Clean", AnnotationViewMode.Clean, currentMode, viewModeSubject));
        toolbar = toolbar.WithView(CreateViewModeButton("Accepted", AnnotationViewMode.Accepted, currentMode, viewModeSubject));
        toolbar = toolbar.WithView(CreateViewModeButton("Original", AnnotationViewMode.Original, currentMode, viewModeSubject));

        // Separator
        toolbar = toolbar.WithView(Controls.Html("<div style=\"width: 1px; height: 20px; background: var(--neutral-stroke-rest); margin: 0 8px;\"></div>"));

        // Accept All button - persists changes via DataChangeRequest
        toolbar = toolbar.WithView(
            Controls.Button("✓ Accept All")
                .WithAppearance(Appearance.Stealth)
                .WithStyle("color: #22c55e; font-size: 0.85rem;")
                .WithClickAction(_ =>
                {
                    if (node == null) return;
                    var acceptedContent = AnnotationMarkdownExtension.GetAcceptedContent(rawContent);
                    var updatedNode = node with { Content = new MarkdownContent { Content = acceptedContent } };
                    host.Hub.Post(
                        new DataChangeRequest().WithUpdates(updatedNode),
                        o => o.WithTarget(host.Hub.Address));
                }));

        // Reject All button - persists changes via DataChangeRequest
        toolbar = toolbar.WithView(
            Controls.Button("✗ Reject All")
                .WithAppearance(Appearance.Stealth)
                .WithStyle("color: #ef4444; font-size: 0.85rem;")
                .WithClickAction(_ =>
                {
                    if (node == null) return;
                    var rejectedContent = AnnotationMarkdownExtension.GetRejectedContent(rawContent);
                    var updatedNode = node with { Content = new MarkdownContent { Content = rejectedContent } };
                    host.Hub.Post(
                        new DataChangeRequest().WithUpdates(updatedNode),
                        o => o.WithTarget(host.Hub.Address));
                }));

        return toolbar;
    }

    /// <summary>
    /// Creates a view mode button with appropriate styling.
    /// </summary>
    private static UiControl CreateViewModeButton(string label, AnnotationViewMode mode, AnnotationViewMode currentMode, ViewModeSubject viewModeSubject)
    {
        var isActive = mode == currentMode;
        var style = isActive
            ? "background: var(--accent-fill-rest); color: white; border: 1px solid var(--accent-fill-rest); padding: 4px 12px; border-radius: 4px; font-size: 0.85rem;"
            : "background: var(--neutral-layer-1); border: 1px solid var(--neutral-stroke-rest); padding: 4px 12px; border-radius: 4px; font-size: 0.85rem;";

        return Controls.Button(label)
            .WithAppearance(Appearance.Stealth)
            .WithStyle(style)
            .WithClickAction(_ => viewModeSubject.OnNext(mode));
    }

    private static UiControl BuildActionMenu(LayoutAreaHost host, MeshNode? node)
    {
        var nodePath = node?.Path ?? host.Hub.Address.ToString();

        // Start with the trigger button (MoreHorizontal icon) - icon-only mode hides the chevron
        var menu = Controls.MenuItem("", FluentIcons.MoreHorizontal(IconSize.Size20))
            .WithAppearance(Appearance.Stealth)
            .WithIconOnly();

        // Edit option - markdown editing with MarkdownEditorControl
        var editHref = $"/{nodePath}/{EditArea}";
        menu = menu.WithView(new NavLinkControl("Edit", FluentIcons.Edit(IconSize.Size16), editHref));

        // Metadata option
        var metadataHref = $"/{nodePath}/{MetadataArea}";
        menu = menu.WithView(new NavLinkControl("Metadata", FluentIcons.DocumentEdit(IconSize.Size16), metadataHref));

        // Notebook option
        var notebookHref = $"/{nodePath}/{NotebookArea}";
        menu = menu.WithView(new NavLinkControl("Notebook", FluentIcons.Code(IconSize.Size16), notebookHref));

        // Comments option
        var commentsHref = $"/{nodePath}/{CommentsArea}";
        menu = menu.WithView(new NavLinkControl("Comments", FluentIcons.Comment(IconSize.Size16), commentsHref));

        // Attachments option
        var attachmentsHref = $"/{nodePath}/{AttachmentsArea}";
        menu = menu.WithView(new NavLinkControl("Attachments", FluentIcons.Attach(IconSize.Size16), attachmentsHref));

        // Settings option
        var settingsHref = $"/{nodePath}/{MeshNodeLayoutAreas.SettingsArea}";
        menu = menu.WithView(new NavLinkControl("Settings", FluentIcons.Settings(IconSize.Size16), settingsHref));

        // Properties option (node metadata from MeshNodeLayoutAreas)
        var propertiesHref = $"/{nodePath}/{MeshNodeLayoutAreas.MetadataArea}";
        menu = menu.WithView(new NavLinkControl("Properties", FluentIcons.Info(IconSize.Size16), propertiesHref));

        return menu;
    }

    /// <summary>
    /// Renders the markdown edit view with auto-save and back button.
    /// Changes are saved automatically as you type (debounced).
    /// </summary>
    public static UiControl MarkdownEdit(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var hubAddress = host.Hub.Address;
        var readHref = $"/{hubPath}/{ReadArea}";

        // Subscribe to node data
        var nodeStream = host.Workspace.GetStream<MeshNode>()
            ?? Observable.Return<MeshNode[]?>(null);

        return Controls.Stack
            .WithWidth("100%")
            .WithHeight("100%")
            .WithView((h, ctx) => nodeStream.Take(1).Select(nodes =>
            {
                var node = nodes?.FirstOrDefault(n => n.Path == hubPath);
                var content = GetMarkdownContent(node);

                return BuildMarkdownEditContent(host, node, hubPath, hubAddress, readHref, content);
            }));
    }

    private static UiControl BuildMarkdownEditContent(
        LayoutAreaHost _,
        MeshNode? node,
        string hubPath,
        Address hubAddress,
        string _1,
        string initialContent)
    {
        var nodeName = node?.Name ?? hubPath.Split('/').LastOrDefault() ?? "Document";
        var backHref = $"/{hubPath}"; // Back to node without area (default Read view)

        // Outer container fills 100% height
        var container = Controls.Stack
            .WithWidth("100%")
            .WithHeight("100%");

        // Header row with back button and node name - minimal padding for full-width editor
        var headerRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithWidth("100%")
            .WithHeight("48px")
            .WithVerticalAlignment(VerticalAlignment.Center)
            .WithHorizontalGap(12)
            .WithStyle("padding: 0 8px; border-bottom: 1px solid var(--neutral-stroke-rest); flex-shrink: 0;");

        // Back button (goes to /path, no area)
        headerRow = headerRow.WithView(Controls.Button("")
            .WithIconStart(FluentIcons.ArrowLeft(IconSize.Size16))
            .WithAppearance(Appearance.Stealth)
            .WithNavigateToHref(backHref));

        // Node name
        headerRow = headerRow.WithView(
            Controls.Html($"<h2 style=\"margin: 0; font-size: 1.1rem; font-weight: 600;\">{System.Web.HttpUtility.HtmlEncode(nodeName)}</h2>"));

        // Spacer
        headerRow = headerRow.WithView(Controls.Html("<div style=\"flex: 1;\"></div>"));

        // Auto-save indicator
        headerRow = headerRow.WithView(
            Controls.Html("<span style=\"color: var(--neutral-foreground-hint); font-size: 0.85rem;\">Changes are saved automatically</span>"));

        container = container.WithView(headerRow);

        // MarkdownEditorControl - calc height to fill available space
        // Configure auto-save with hub address and node path
        var editor = new MarkdownEditorControl()
            .WithDocumentId(hubPath)
            .WithValue(initialContent)
            .WithHeight("calc(100vh - 150px)")
            .WithMaxHeight("none")
            .WithTrackChanges(true)
            .WithPlaceholder("Start writing your markdown content...")
            .WithAutoSave(hubAddress.ToString(), hubPath);

        // Wrap editor in full-width container with no padding
        var editorWrapper = Controls.Stack
            .WithWidth("100%")
            .WithStyle("flex: 1; width: 100%; padding: 0;")
            .WithView(editor);

        container = container.WithView(editorWrapper);

        return container;
    }

    /// <summary>
    /// Edit mode tabs for metadata view.
    /// </summary>
    public enum EditTab
    {
        Markdown,
        Metadata
    }

    /// <summary>
    /// Renders the metadata edit view for editing node properties.
    /// </summary>
    public static UiControl MetadataView(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var hubAddress = host.Hub.Address;
        var readHref = $"/{hubPath}/{ReadArea}";

        // Data IDs for editable fields
        var nameDataId = $"edit-name-{hubPath.Replace("/", "-")}";
        var contentDataId = $"edit-content-{hubPath.Replace("/", "-")}";
        var descriptionDataId = $"edit-description-{hubPath.Replace("/", "-")}";
        var categoryDataId = $"edit-category-{hubPath.Replace("/", "-")}";
        var iconDataId = $"edit-icon-{hubPath.Replace("/", "-")}";

        // Tab state
        var tabSubject = new BehaviorSubject<EditTab>(EditTab.Markdown);
        host.RegisterForDisposal(tabSubject);

        // Subscribe to node data
        var nodeStream = host.Workspace.GetStream<MeshNode>()
            ?? Observable.Return<MeshNode[]?>(null);

        return Controls.Stack
            .WithWidth("100%")
            .WithHeight("100%")
            .WithView((h, ctx) => nodeStream.Take(1).CombineLatest(tabSubject, (nodes, tab) =>
            {
                var node = nodes?.FirstOrDefault(n => n.Path == hubPath);

                // Initialize data streams on first load
                var content = GetMarkdownContent(node);
                host.UpdateData(nameDataId, node?.Name ?? "");
                host.UpdateData(contentDataId, content);
                host.UpdateData(descriptionDataId, node?.Description ?? "");
                host.UpdateData(categoryDataId, node?.Category ?? "");
                host.UpdateData(iconDataId, node?.Icon ?? "");

                return BuildEditContent(host, node, hubPath, hubAddress, readHref, tab, tabSubject,
                    nameDataId, contentDataId, descriptionDataId, categoryDataId, iconDataId);
            }));
    }

    private static UiControl BuildEditContent(
        LayoutAreaHost host,
        MeshNode? node,
        string hubPath,
        Address hubAddress,
        string readHref,
        EditTab currentTab,
        BehaviorSubject<EditTab> tabSubject,
        string nameDataId,
        string contentDataId,
        string descriptionDataId,
        string categoryDataId,
        string iconDataId)
    {
        var container = Controls.Stack
            .WithWidth("100%")
            .WithHeight("100%");

        // Header row: Name field
        var headerRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithWidth("100%")
            .WithHeight("40px")
            .WithVerticalAlignment(VerticalAlignment.Center)
            .WithHorizontalGap(8);

        headerRow = headerRow.WithView(
            new TextFieldControl(new JsonPointerReference(""))
                .WithPlaceholder("Document name...")
                .WithImmediate(true)
                .WithStyle("flex: 1; font-size: 1.1rem; font-weight: 600;")
                with
            { DataContext = LayoutAreaReference.GetDataPointer(nameDataId) });

        container = container.WithView(headerRow);

        // Toolbar row: Tabs and track changes
        var toolbar = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithWidth("100%")
            .WithHeight("40px")
            .WithVerticalAlignment(VerticalAlignment.Center)
            .WithHorizontalGap(8);

        // Tab buttons
        toolbar = toolbar.WithView(
            Controls.Button("Markdown")
                .WithAppearance(currentTab == EditTab.Markdown ? Appearance.Accent : Appearance.Stealth)
                .WithClickAction(_ => tabSubject.OnNext(EditTab.Markdown)));

        toolbar = toolbar.WithView(
            Controls.Button("Metadata")
                .WithAppearance(currentTab == EditTab.Metadata ? Appearance.Accent : Appearance.Stealth)
                .WithClickAction(_ => tabSubject.OnNext(EditTab.Metadata)));

        container = container.WithView(toolbar);

        // Content area based on tab
        if (currentTab == EditTab.Markdown)
        {
            // Monaco editor for markdown - fill remaining space
            var editor = new MarkdownEditorControl()
                .WithValue(host.Stream.GetDataStream<string>(contentDataId).Take(1).Wait() ?? "")
                .WithDocumentId(hubPath)
                .WithHeight("100%")
                .WithMaxHeight("none")
                .WithTrackChanges(false)
                .WithPlaceholder("Start writing your markdown content...");

            var editorWrapper = Controls.Stack
                .WithWidth("100%")
                .WithHeight("calc(100% - 128px)")
                .WithView(editor);

            container = container.WithView(editorWrapper);
        }
        else
        {
            // Metadata form
            var metadataForm = Controls.Stack
                .WithWidth("100%")
                .WithHeight("calc(100% - 128px)")
                .WithStyle("overflow-y: auto; padding: 16px;");

            var formStyle = "display: grid; grid-template-columns: 120px 1fr; gap: 12px; align-items: center; margin-bottom: 12px;";

            // Description
            metadataForm = metadataForm.WithView(Controls.Stack
                .WithStyle(formStyle)
                .WithView(Controls.Html("<label style=\"font-weight: 500;\">Description:</label>"))
                .WithView(new TextAreaControl(new JsonPointerReference(""))
                    .WithPlaceholder("Enter description...")
                    .WithRows(3)
                    .WithImmediate(true) with
                { DataContext = LayoutAreaReference.GetDataPointer(descriptionDataId) }));

            // Category
            metadataForm = metadataForm.WithView(Controls.Stack
                .WithStyle(formStyle)
                .WithView(Controls.Html("<label style=\"font-weight: 500;\">Category:</label>"))
                .WithView(new TextFieldControl(new JsonPointerReference(""))
                    .WithPlaceholder("e.g., Documentation, Tutorial...")
                    .WithImmediate(true) with
                { DataContext = LayoutAreaReference.GetDataPointer(categoryDataId) }));

            // Icon
            metadataForm = metadataForm.WithView(Controls.Stack
                .WithStyle(formStyle)
                .WithView(Controls.Html("<label style=\"font-weight: 500;\">Icon:</label>"))
                .WithView(new TextFieldControl(new JsonPointerReference(""))
                    .WithPlaceholder("e.g., Document, Book...")
                    .WithImmediate(true) with
                { DataContext = LayoutAreaReference.GetDataPointer(iconDataId) }));

            // Read-only info
            metadataForm = metadataForm.WithView(Controls.Html($@"
                <div style=""margin-top: 24px; padding: 16px; background: var(--neutral-layer-2); border-radius: 8px;"">
                    <h4 style=""margin: 0 0 12px 0;"">Node Information</h4>
                    <div style=""display: grid; grid-template-columns: 120px 1fr; gap: 8px; font-size: 0.9rem;"">
                        <span style=""color: var(--neutral-foreground-hint);"">Path:</span>
                        <span>{System.Web.HttpUtility.HtmlEncode(hubPath)}</span>
                        <span style=""color: var(--neutral-foreground-hint);"">Node Type:</span>
                        <span>{System.Web.HttpUtility.HtmlEncode(node?.NodeType ?? "Markdown")}</span>
                        <span style=""color: var(--neutral-foreground-hint);"">ID:</span>
                        <span>{System.Web.HttpUtility.HtmlEncode(node?.Id ?? "")}</span>
                    </div>
                </div>
            "));

            container = container.WithView(metadataForm);
        }

        // Button row at the bottom
        var buttonRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithWidth("100%")
            .WithHeight("48px")
            .WithVerticalAlignment(VerticalAlignment.Center)
            .WithHorizontalGap(12);

        // Save button
        buttonRow = buttonRow.WithView(Controls.Button("Save")
            .WithAppearance(Appearance.Accent)
            .WithIconStart(FluentIcons.Save())
            .WithClickAction(async actx =>
            {
                // Get all field values
                var newName = await host.Stream.GetDataStream<string>(nameDataId).FirstAsync();
                var newContent = await host.Stream.GetDataStream<string>(contentDataId).FirstAsync();
                var newDescription = await host.Stream.GetDataStream<string>(descriptionDataId).FirstAsync();
                var newCategory = await host.Stream.GetDataStream<string>(categoryDataId).FirstAsync();
                var newIcon = await host.Stream.GetDataStream<string>(iconDataId).FirstAsync();

                // Get the current node
                var nodeStream = host.Workspace.GetStream<MeshNode>();
                var nodes = nodeStream != null ? await nodeStream.FirstAsync() : null;
                var currentNode = nodes?.FirstOrDefault(n => n.Path == hubPath);

                if (currentNode == null)
                {
                    var errorDialog = Controls.Dialog(
                        Controls.Markdown("**Error:** No document found to save."),
                        "Save Failed"
                    ).WithSize("M");
                    actx.Host.UpdateArea(DialogControl.DialogArea, errorDialog);
                    return;
                }

                // Create updated node
                var updatedNode = currentNode with
                {
                    Name = string.IsNullOrWhiteSpace(newName) ? null : newName,
                    Description = string.IsNullOrWhiteSpace(newDescription) ? null : newDescription,
                    Category = string.IsNullOrWhiteSpace(newCategory) ? null : newCategory,
                    Icon = string.IsNullOrWhiteSpace(newIcon) ? null : newIcon,
                    Content = new MarkdownContent { Content = newContent ?? "" }
                };

                using var cts = new CancellationTokenSource(10.Seconds());
                try
                {
                    var response = await actx.Host.Hub.AwaitResponse<DataChangeResponse>(
                        new DataChangeRequest().WithUpdates(updatedNode),
                        o => o.WithTarget(hubAddress),
                        cts.Token);

                    if (response.Message.Log.Status != ActivityStatus.Succeeded)
                    {
                        var errorDialog = Controls.Dialog(
                            Controls.Markdown($"**Error saving:**\n\n{response.Message.Log}"),
                            "Save Failed"
                        ).WithSize("M");
                        actx.Host.UpdateArea(DialogControl.DialogArea, errorDialog);
                        return;
                    }

                    // Navigate back to read view
                    actx.Host.UpdateArea(actx.Area, new RedirectControl(readHref));
                }
                catch (Exception ex)
                {
                    var errorDialog = Controls.Dialog(
                        Controls.Markdown($"**Error saving:**\n\n{ex.Message}"),
                        "Save Failed"
                    ).WithSize("M");
                    actx.Host.UpdateArea(DialogControl.DialogArea, errorDialog);
                }
            }));

        // Cancel button
        buttonRow = buttonRow.WithView(Controls.Button("Cancel")
            .WithAppearance(Appearance.Neutral)
            .WithNavigateToHref(readHref));

        container = container.WithView(buttonRow);

        return container;
    }

    /// <summary>
    /// Renders the markdown content as a notebook with code and markdown cells.
    /// Uses NotebookParser to split the content into cells.
    /// </summary>
    public static IObservable<UiControl?> NotebookView(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return<MeshNode[]>(Array.Empty<MeshNode>());

        return nodeStream.Select(nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            return BuildNotebookView(host, node);
        });
    }

    private static UiControl BuildNotebookView(LayoutAreaHost host, MeshNode? node)
    {
        var nodePath = node?.Path ?? host.Hub.Address.ToString();
        var title = node?.Name ?? "Notebook";

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
                .WithNavigateToHref(readHref));

        headerStack = headerStack.WithView(
            Controls.Html($"<h2 style=\"margin: 0; font-size: 1.25rem;\">{System.Web.HttpUtility.HtmlEncode(title)}</h2>"));

        container = container.WithView(headerStack);

        // Get markdown content and parse into cells
        var content = GetMarkdownContent(node);
        var cells = NotebookParser.ParseMarkdown(content ?? string.Empty);

        // Create the notebook control
        var notebook = new NotebookControl()
            .WithCells(cells)
            .WithDefaultLanguage("csharp")
            .WithAvailableLanguages("csharp", "python", "javascript", "typescript", "fsharp", "markdown")
            .WithShowLineNumbers(true)
            .WithHeight("100%");

        // Notebook area
        var notebookArea = Controls.Stack
            .WithWidth("100%")
            .WithStyle("flex: 1; padding: 16px; overflow: auto; box-sizing: border-box;")
            .WithView(notebook);

        container = container.WithView(notebookArea);

        return container;
    }

    /// <summary>
    /// Renders the comments view showing all comments on the document.
    /// </summary>
    public static IObservable<UiControl?> CommentsView(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return<MeshNode[]>(Array.Empty<MeshNode>());

        return nodeStream.Select(nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            return BuildCommentsView(host, node);
        });
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
                .WithNavigateToHref(readHref));

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

        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return<MeshNode[]>(Array.Empty<MeshNode>());

        return nodeStream.Select(nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            return BuildAttachmentsView(host, node);
        });
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
                .WithNavigateToHref(readHref));

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
    /// Uses MeshNodeThumbnailControl for consistent styling with the catalog.
    /// </summary>
    public static UiControl Thumbnail(LayoutAreaHost host, RenderingContext ctx)
    {
        var hubPath = host.Hub.Address.ToString();

        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return<MeshNode[]>(Array.Empty<MeshNode>());

        return Controls.Stack
            .WithView((h, c) => nodeStream.Select(nodes =>
            {
                var node = nodes.FirstOrDefault(n => n.Path == hubPath);
                return MeshNodeThumbnailControl.FromNode(node, hubPath);
            }));
    }

    /// <summary>
    /// Extracts markdown content from a MeshNode.
    /// Handles plain string content and MarkdownDocument JSON format.
    /// </summary>
    private static string GetMarkdownContent(MeshNode? node)
    {
        if (node?.Content == null)
            return string.Empty;

        // Handle MarkdownContent (from MarkdownFileParser)
        if (node.Content is MarkdownContent markdownContent)
            return markdownContent.Content;

        // Handle plain string content
        if (node.Content is string stringContent)
            return stringContent;

        // Handle MarkdownDocument content (JSON with $type and content fields)
        if (node.Content is System.Text.Json.JsonElement jsonElement)
        {
            // Check for string JSON element
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

        // Fall back to Description
        return node.Description ?? string.Empty;
    }
}
