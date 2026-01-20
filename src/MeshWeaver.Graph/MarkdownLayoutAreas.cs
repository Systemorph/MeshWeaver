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
                .WithView(MeshNodeView.SettingsArea, MeshNodeView.Settings)
                .WithView(MeshNodeView.MetadataArea, MeshNodeView.Metadata)
                .WithView(MeshNodeView.ThumbnailArea, Thumbnail));

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
        /// IDs of annotations that are expanded (collapsed by default).
        /// </summary>
        public IReadOnlySet<string> ExpandedAnnotationIds { get; init; }
            = new HashSet<string>();

        /// <summary>
        /// Replies that have been added (in-memory for now).
        /// </summary>
        public IReadOnlyDictionary<string, List<(string Author, string Text, DateTimeOffset Time)>> Replies { get; init; }
            = new Dictionary<string, List<(string Author, string Text, DateTimeOffset Time)>>();
    }

    /// <summary>
    /// Model for the reply form.
    /// </summary>
    public record ReplyFormModel
    {
        public string Text { get; init; } = string.Empty;
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

        // Combine node stream with view mode, panel state, and children for reactive updates
        return nodeStream
            .CombineLatest(viewModeSubject, panelStateSubject, childrenStream, (nodes, viewMode, panelState, children) =>
            {
                var node = nodes.FirstOrDefault(n => n.Path == hubPath);
                return BuildReadView(host, node, viewMode, viewModeSubject, panelState, panelStateSubject, children ?? Array.Empty<MeshNode>());
            });
    }

    private static UiControl BuildReadView(
        LayoutAreaHost host,
        MeshNode? node,
        AnnotationViewMode viewMode,
        ViewModeSubject viewModeSubject,
        AnnotationPanelState panelState,
        BehaviorSubject<AnnotationPanelState> panelStateSubject,
        IReadOnlyList<MeshNode> markdownChildren)
    {
        var nodePath = node?.Path ?? host.Hub.Address.ToString();
        var containerId = $"markdown-container-{Guid.NewGuid():N}";

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

        // If we have annotations and in markup mode, use a split layout (Word-style)
        if (hasAnnotations && annotations.Count > 0 && viewMode == AnnotationViewMode.Markup)
        {
            return BuildSplitLayoutWithAnnotations(host, node, content, containerId, annotations, viewModeSubject, viewMode, panelState, panelStateSubject, markdownChildren);
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
            container = container.WithView(BuildReactiveViewModeToolbar(viewModeSubject, viewMode, annotations));
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
            var displayName = MeshNodeView.GetGroupDisplayName(group.Key, group.Count());

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
    /// Builds the split layout with content on the left and inline annotations on the right (Word-style).
    /// Annotations are positioned at the same height as their markers with connecting lines.
    /// </summary>
    private static UiControl BuildSplitLayoutWithAnnotations(
        LayoutAreaHost host,
        MeshNode? node,
        string content,
        string containerId,
        List<ParsedAnnotation> annotations,
        ViewModeSubject viewModeSubject,
        AnnotationViewMode currentViewMode,
        AnnotationPanelState panelState,
        BehaviorSubject<AnnotationPanelState> panelStateSubject,
        IReadOnlyList<MeshNode> markdownChildren)
    {
        var nodePath = node?.Path ?? host.Hub.Address.ToString();
        // Outer container - centered with max width
        var outerContainer = Controls.Stack
            .WithWidth("100%")
            .WithStyle("max-width: 1200px; margin: 0 auto; padding: 24px; background: var(--neutral-layer-1); position: relative;");

        // Action menu positioned at top-right of content area
        var actionMenu = Controls.Stack
            .WithStyle("position: absolute; top: 24px; right: 24px; z-index: 10;")
            .WithView(BuildActionMenu(host, node));

        outerContainer = outerContainer.WithView(actionMenu);

        // Reactive view mode toolbar
        outerContainer = outerContainer.WithView(BuildReactiveViewModeToolbar(viewModeSubject, currentViewMode, annotations));

        // Split layout using nested stacks with position:relative for SVG lines
        var splitLayout = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithWidth("100%")
            .WithStyle("gap: 16px; align-items: flex-start; position: relative;");

        // Left: Content area with markdown
        var contentArea = Controls.Stack
            .WithStyle("flex: 7; min-width: 0; max-width: 700px; position: relative;")
            .WithView(Controls.Html($"<div id=\"{containerId}\" class=\"markdown-annotations-container\" style=\"line-height: 1.7; font-size: 1rem;\">"))
            .WithView(new MarkdownControl(content))
            .WithView(Controls.Html("</div>"));

        // Right: Annotations column - narrower for compact cards
        var annotationsColumn = Controls.Stack
            .WithStyle("flex: 0 0 260px; min-width: 200px; max-width: 280px; position: relative; padding-left: 8px;");

        // Add open tag for annotations container
        annotationsColumn = annotationsColumn.WithView(
            Controls.Html($"<div id=\"{containerId}-annotations\" class=\"annotations-column\">"));

        // Build annotation cards as UiControls
        foreach (var annotation in annotations)
        {
            annotationsColumn = annotationsColumn.WithView(
                BuildAnnotationCard(host, annotation, panelState, panelStateSubject, viewModeSubject));
        }

        // Close annotations container
        annotationsColumn = annotationsColumn.WithView(Controls.Html("</div>"));

        splitLayout = splitLayout.WithView(contentArea);
        splitLayout = splitLayout.WithView(annotationsColumn);

        outerContainer = outerContainer.WithView(splitLayout);

        // Add JavaScript for positioning and connecting lines
        outerContainer = outerContainer.WithView(BuildInlineAnnotationScript(containerId));

        // Add Markdown children section if there are any
        if (markdownChildren.Count > 0)
        {
            outerContainer = outerContainer.WithView(BuildMarkdownChildrenSection(markdownChildren, nodePath));
        }

        return outerContainer;
    }

    /// <summary>
    /// Builds a collapsible annotation card with reactive buttons.
    /// </summary>
    private static UiControl BuildAnnotationCard(
        LayoutAreaHost host,
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

        // Use simple symbols instead of emojis for cleaner look
        var typeSymbol = annotation.Type switch
        {
            AnnotationType.Comment => "C",
            AnnotationType.Insert => "+",
            AnnotationType.Delete => "−",
            _ => "•"
        };

        var author = annotation.Author ?? "";
        var shortAuthor = author.Length > 8 ? author[..6] + ".." : author;
        var shortPreview = annotation.HighlightedText.Length > 15
            ? annotation.HighlightedText[..12] + "..."
            : annotation.HighlightedText;

        var isExpanded = panelState.ExpandedAnnotationIds.Contains(annotation.Id);
        var isReplyingToThis = panelState.ReplyingToAnnotationId == annotation.Id;
        var replyCount = panelState.Replies.TryGetValue(annotation.Id, out var replies) ? replies.Count : 0;

        // Toggle expansion function
        void ToggleExpand()
        {
            var newExpanded = new HashSet<string>(panelState.ExpandedAnnotationIds);
            if (isExpanded)
                newExpanded.Remove(annotation.Id);
            else
                newExpanded.Add(annotation.Id);
            panelStateSubject.OnNext(panelState with { ExpandedAnnotationIds = newExpanded });
        }

        // Build the card container - very compact when collapsed (like Word's sidebar)
        var cardStyle = isExpanded
            ? $"padding: 6px 8px; background: var(--neutral-layer-1); border-radius: 3px; border-left: 3px solid {typeColor}; margin-bottom: 2px; box-shadow: 0 1px 3px rgba(0,0,0,0.1);"
            : $"padding: 2px 6px; background: var(--neutral-layer-1); border-radius: 3px; border-left: 2px solid {typeColor}; margin-bottom: 2px; opacity: 0.85;";

        var card = Controls.Stack.WithStyle(cardStyle);

        // Collapsed header - minimal info, single line
        var headerContent = isExpanded
            ? $"<span style=\"display:inline-flex;align-items:center;justify-content:center;width:14px;height:14px;border-radius:50%;background:{typeColor};color:white;font-size:9px;font-weight:bold;margin-right:4px;\">{typeSymbol}</span><b style=\"color:{typeColor};font-size:0.7rem;\">{System.Web.HttpUtility.HtmlEncode(shortAuthor)}</b>: \"{System.Web.HttpUtility.HtmlEncode(shortPreview)}\""
            : $"<span style=\"display:inline-flex;align-items:center;justify-content:center;width:12px;height:12px;border-radius:50%;background:{typeColor};color:white;font-size:8px;font-weight:bold;margin-right:3px;\">{typeSymbol}</span><span style=\"font-size:0.65rem;color:var(--neutral-foreground-hint);\">{System.Web.HttpUtility.HtmlEncode(shortAuthor)}</span>" + (replyCount > 0 ? $"<span style=\"font-size:0.6rem;color:var(--accent-fill-rest);margin-left:2px;\">+{replyCount}</span>" : "");

        var headerStyle = isExpanded
            ? "display: flex; align-items: center; gap: 2px; font-size: 0.7rem; cursor: pointer; user-select: none; line-height: 1.2;"
            : "display: flex; align-items: center; gap: 2px; font-size: 0.65rem; cursor: pointer; user-select: none; white-space: nowrap; overflow: hidden; line-height: 1.1;";

        card = card.WithView(
            Controls.Html($"<div style=\"{headerStyle}\">{headerContent}</div>")
        );

        // Make header clickable
        card = card.WithClickAction(_ => ToggleExpand());

        // Expanded content
        if (isExpanded)
        {
            // Comment text (more compact)
            if (!string.IsNullOrEmpty(annotation.CommentText))
            {
                card = card.WithView(Controls.Html($@"
                    <div style=""font-size: 0.7rem; color: var(--neutral-foreground-rest); margin: 4px 0; padding: 3px 5px; background: var(--neutral-layer-2); border-radius: 2px; line-height: 1.3;"">
                        {System.Web.HttpUtility.HtmlEncode(annotation.CommentText)}
                    </div>"));
            }

            // Replies (compact)
            if (replies != null && replies.Count > 0)
            {
                foreach (var reply in replies)
                {
                    card = card.WithView(Controls.Html($@"
                        <div style=""font-size: 0.65rem; padding: 2px 4px; margin: 2px 0; background: var(--neutral-layer-3);
                                   border-radius: 2px; border-left: 2px solid var(--accent-fill-rest); line-height: 1.2;"">
                            <span style=""font-weight: 500; color: var(--accent-fill-rest);"">{System.Web.HttpUtility.HtmlEncode(reply.Author)}</span>: {System.Web.HttpUtility.HtmlEncode(reply.Text)}
                        </div>"));
                }
            }

            // Reply form or action buttons (compact)
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
                        .WithStyle("gap: 3px; margin-top: 3px;");

                    buttonRow = buttonRow.WithView(
                        Controls.Button("Reply")
                            .WithStyle("padding: 1px 5px; font-size: 0.6rem;")
                            .WithClickAction(_ => panelStateSubject.OnNext(
                                panelState with { ReplyingToAnnotationId = annotation.Id })));

                    buttonRow = buttonRow.WithView(
                        Controls.Button("✓")
                            .WithAppearance(Appearance.Stealth)
                            .WithStyle("padding: 1px 5px; font-size: 0.6rem; border: 1px solid var(--neutral-stroke-rest);"));

                    card = card.WithView(buttonRow);
                }
            }
            else
            {
                var buttonRow = Controls.Stack
                    .WithOrientation(Orientation.Horizontal)
                    .WithStyle("gap: 3px; margin-top: 3px;");

                buttonRow = buttonRow.WithView(
                    Controls.Button("✓")
                        .WithStyle("padding: 1px 6px; font-size: 0.6rem; background: #22c55e; color: white;")
                        .WithClickAction(_ => viewModeSubject.OnNext(AnnotationViewMode.Accepted)));

                buttonRow = buttonRow.WithView(
                    Controls.Button("✗")
                        .WithStyle("padding: 1px 6px; font-size: 0.6rem; background: #ef4444; color: white;")
                        .WithClickAction(_ => viewModeSubject.OnNext(AnnotationViewMode.Original)));

                card = card.WithView(buttonRow);
            }
        }

        // Wrap in a div with data attributes for positioning and line drawing
        return Controls.Stack
            .WithView(Controls.Html($"<div class=\"annotation-card\" data-annotation-id=\"{annotation.Id}\" data-annotation-type=\"{annotation.Type}\" data-color=\"{typeColor}\">"))
            .WithView(card)
            .WithView(Controls.Html("</div>"));
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
            .WithStyle("margin-top: 8px; padding: 8px; background: var(--neutral-layer-2); border-radius: 4px;");

        // Reply textarea using host.Edit with ReplyFormModel
        var editControl = host.Edit(new ReplyFormModel(), replyDataId);
        if (editControl != null)
        {
            var editWrapper = Controls.Stack
                .WithStyle("margin-bottom: 8px;")
                .WithView(editControl);
            form = form.WithView(editWrapper);
        }

        // Button row
        var buttonRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 6px; justify-content: flex-end;");

        buttonRow = buttonRow.WithView(
            Controls.Button("Cancel")
                .WithAppearance(Appearance.Stealth)
                .WithStyle("padding: 4px 12px; font-size: 0.75rem;")
                .WithClickAction(_ => panelStateSubject.OnNext(
                    panelState with { ReplyingToAnnotationId = null })));

        buttonRow = buttonRow.WithView(
            Controls.Button("Submit")
                .WithStyle("padding: 4px 12px; font-size: 0.75rem;")
                .WithClickAction(async ctx =>
                {
                    // Get the reply text from the form data
                    var replyModel = await ctx.Host.Stream.GetDataAsync<ReplyFormModel>(replyDataId);
                    if (replyModel != null && !string.IsNullOrWhiteSpace(replyModel.Text))
                    {
                        // Add the reply to the state
                        var newReplies = new Dictionary<string, List<(string Author, string Text, DateTimeOffset Time)>>(panelState.Replies);
                        if (!newReplies.ContainsKey(annotationId))
                        {
                            newReplies[annotationId] = new List<(string, string, DateTimeOffset)>();
                        }
                        newReplies[annotationId].Add(("You", replyModel.Text, DateTimeOffset.Now));

                        panelStateSubject.OnNext(new AnnotationPanelState
                        {
                            ReplyingToAnnotationId = null,
                            Replies = newReplies
                        });
                    }
                }));

        form = form.WithView(buttonRow);
        return form;
    }

    /// <summary>
    /// Builds JavaScript for positioning annotation cards and drawing SVG connecting lines.
    /// </summary>
    private static UiControl BuildInlineAnnotationScript(string containerId)
    {
        return Controls.Html($@"
<script>
(function() {{
    var containerId = '{containerId}';
    var minCardGap = 1; // Minimal gap between cards

    function drawConnectingLines() {{
        var container = document.getElementById(containerId);
        var annotationsCol = document.getElementById(containerId + '-annotations');
        if (!container || !annotationsCol) return;

        // Remove old SVG if exists
        var oldSvg = document.getElementById(containerId + '-lines');
        if (oldSvg) oldSvg.remove();

        // Create SVG in the split layout parent
        var splitLayout = annotationsCol.parentElement;
        if (!splitLayout) return;

        var svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
        svg.id = containerId + '-lines';
        svg.style.cssText = 'position:absolute;top:0;left:0;width:100%;height:100%;pointer-events:none;z-index:1;overflow:visible;';
        splitLayout.insertBefore(svg, splitLayout.firstChild);

        var splitRect = splitLayout.getBoundingClientRect();
        var containerRect = container.getBoundingClientRect();

        // Update SVG size
        svg.setAttribute('width', splitLayout.scrollWidth);
        svg.setAttribute('height', Math.max(splitLayout.scrollHeight, container.scrollHeight, annotationsCol.scrollHeight));

        var cards = annotationsCol.querySelectorAll('.annotation-card');
        cards.forEach(function(card) {{
            var annotationId = card.dataset.annotationId;
            var color = card.dataset.color || '#6b7280';
            var marker = container.querySelector('[data-comment-id=""' + annotationId + '""]') ||
                         container.querySelector('[data-change-id=""' + annotationId + '""]');

            if (marker && card.offsetParent) {{
                var markerRect = marker.getBoundingClientRect();
                var cardRect = card.getBoundingClientRect();

                // Calculate positions relative to split layout
                var markerX = markerRect.right - splitRect.left + 2;
                var markerY = markerRect.top + markerRect.height / 2 - splitRect.top;
                var cardX = cardRect.left - splitRect.left - 2;
                var cardY = cardRect.top + 8 - splitRect.top;

                // Simple horizontal line with small turn
                var path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
                var midX = markerX + 10;
                path.setAttribute('d', 'M ' + markerX + ' ' + markerY + ' H ' + midX + ' L ' + cardX + ' ' + cardY);
                path.setAttribute('stroke', color);
                path.setAttribute('stroke-width', '1');
                path.setAttribute('fill', 'none');
                path.setAttribute('opacity', '0.4');
                path.setAttribute('data-annotation', annotationId);
                svg.appendChild(path);

                // Small dot at marker end
                var circle = document.createElementNS('http://www.w3.org/2000/svg', 'circle');
                circle.setAttribute('cx', markerX);
                circle.setAttribute('cy', markerY);
                circle.setAttribute('r', '2');
                circle.setAttribute('fill', color);
                circle.setAttribute('opacity', '0.6');
                svg.appendChild(circle);
            }}
        }});
    }}

    function positionAnnotationCards() {{
        var container = document.getElementById(containerId);
        var annotationsCol = document.getElementById(containerId + '-annotations');
        if (!container || !annotationsCol) return;

        var cards = annotationsCol.querySelectorAll('.annotation-card');
        var positions = [];

        // Calculate desired positions based on marker locations
        cards.forEach(function(card) {{
            var annotationId = card.dataset.annotationId;
            var marker = container.querySelector('[data-comment-id=""' + annotationId + '""]') ||
                         container.querySelector('[data-change-id=""' + annotationId + '""]');

            if (marker) {{
                var markerRect = marker.getBoundingClientRect();
                var containerRect = container.getBoundingClientRect();
                var desiredTop = markerRect.top - containerRect.top;

                positions.push({{
                    card: card,
                    marker: marker,
                    annotationId: annotationId,
                    desiredTop: desiredTop,
                    height: card.offsetHeight || 18
                }});
            }}
        }});

        // Sort by desired position
        positions.sort(function(a, b) {{ return a.desiredTop - b.desiredTop; }});

        // Resolve overlaps - push cards down if they would overlap
        var lastBottom = 0;
        positions.forEach(function(pos) {{
            var actualTop = Math.max(pos.desiredTop, lastBottom + minCardGap);
            pos.actualTop = actualTop;
            lastBottom = actualTop + pos.height;
        }});

        // Apply positions
        positions.forEach(function(pos) {{
            pos.card.style.position = 'absolute';
            pos.card.style.top = pos.actualTop + 'px';
            pos.card.style.left = '0';
            pos.card.style.right = '0';
        }});

        // Set minimum height for annotations column
        if (positions.length > 0) {{
            var lastPos = positions[positions.length - 1];
            annotationsCol.style.minHeight = (lastPos.actualTop + lastPos.height + 16) + 'px';
        }}

        // Draw lines after positioning
        requestAnimationFrame(drawConnectingLines);
    }}

    // Initialize
    function init() {{
        setTimeout(positionAnnotationCards, 50);
        setTimeout(positionAnnotationCards, 200);
        setTimeout(positionAnnotationCards, 500);
    }}

    if (document.readyState === 'loading') {{
        document.addEventListener('DOMContentLoaded', init);
    }} else {{
        init();
    }}

    window.addEventListener('resize', function() {{
        requestAnimationFrame(positionAnnotationCards);
    }});

    // Re-run when cards expand/collapse
    var observer = new MutationObserver(function(mutations) {{
        requestAnimationFrame(positionAnnotationCards);
    }});
    setTimeout(function() {{
        var annotationsCol = document.getElementById(containerId + '-annotations');
        if (annotationsCol) {{
            observer.observe(annotationsCol, {{ childList: true, subtree: true, attributes: true, characterData: true }});
        }}
    }}, 100);

    // Highlight on click
    document.addEventListener('click', function(e) {{
        var marker = e.target.closest('[data-comment-id], [data-change-id]');
        if (marker) {{
            var id = marker.dataset.commentId || marker.dataset.changeId;
            highlightAnnotation(id);
        }}
    }});

    window.highlightAnnotation = function(annotationId) {{
        var container = document.getElementById(containerId);
        if (!container) return;

        // Remove previous highlights
        document.querySelectorAll('.annotation-active').forEach(function(el) {{
            el.classList.remove('annotation-active');
        }});
        document.querySelectorAll('.annotation-card.active').forEach(function(el) {{
            el.classList.remove('active');
        }});

        // Highlight marker
        var marker = container.querySelector('[data-comment-id=""' + annotationId + '""]') ||
                     container.querySelector('[data-change-id=""' + annotationId + '""]');
        if (marker) {{
            marker.classList.add('annotation-active');
        }}

        // Highlight card and line
        var card = document.querySelector('.annotation-card[data-annotation-id=""' + annotationId + '""]');
        if (card) {{
            card.classList.add('active');
        }}

        // Highlight the SVG line
        var svg = document.getElementById(containerId + '-lines');
        if (svg) {{
            svg.querySelectorAll('path').forEach(function(p) {{
                if (p.dataset.annotation === annotationId) {{
                    p.setAttribute('opacity', '0.8');
                    p.setAttribute('stroke-width', '2');
                }} else {{
                    p.setAttribute('opacity', '0.3');
                    p.setAttribute('stroke-width', '1');
                }}
            }});
        }}
    }};
}})();
</script>
<style>
.annotations-column {{
    position: relative;
}}
.annotation-card {{
    transition: all 0.15s ease;
}}
.annotation-card:hover {{
    opacity: 1 !important;
    box-shadow: 0 1px 4px rgba(0,0,0,0.12) !important;
    z-index: 10;
}}
.annotation-card.active {{
    opacity: 1 !important;
    box-shadow: 0 0 0 1px var(--accent-fill-rest), 0 1px 4px rgba(0,0,0,0.15) !important;
    z-index: 10;
}}
.annotation-active {{
    background: rgba(59, 130, 246, 0.12) !important;
    box-shadow: 0 0 0 1px var(--accent-fill-rest);
    border-radius: 2px;
}}
</style>
        ");
    }

    /// <summary>
    /// Builds a reactive view mode toolbar that re-renders the markdown on mode change.
    /// </summary>
    private static UiControl BuildReactiveViewModeToolbar(ViewModeSubject viewModeSubject, AnnotationViewMode currentMode, List<ParsedAnnotation> annotations)
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

        // Accept All button - switches to accepted view
        toolbar = toolbar.WithView(
            Controls.Button("✓ Accept All")
                .WithAppearance(Appearance.Stealth)
                .WithStyle("color: #22c55e; font-size: 0.85rem;")
                .WithClickAction(_ => viewModeSubject.OnNext(AnnotationViewMode.Accepted)));

        // Reject All button - switches to original view
        toolbar = toolbar.WithView(
            Controls.Button("✗ Reject All")
                .WithAppearance(Appearance.Stealth)
                .WithStyle("color: #ef4444; font-size: 0.85rem;")
                .WithClickAction(_ => viewModeSubject.OnNext(AnnotationViewMode.Original)));

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
        var settingsHref = $"/{nodePath}/{MeshNodeView.SettingsArea}";
        menu = menu.WithView(new NavLinkControl("Settings", FluentIcons.Settings(IconSize.Size16), settingsHref));

        // Properties option (node metadata from MeshNodeView)
        var propertiesHref = $"/{nodePath}/{MeshNodeView.MetadataArea}";
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
        LayoutAreaHost host,
        MeshNode? node,
        string hubPath,
        Address hubAddress,
        string readHref,
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
                return BuildThumbnail(node, hubPath);
            }));
    }

    private static UiControl BuildThumbnail(MeshNode? node, string hubPath)
    {
        var title = node?.Name ?? hubPath.Split('/').LastOrDefault() ?? "Document";
        var description = node?.Description ?? "";
        var iconName = node?.Icon ?? "Document";

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
