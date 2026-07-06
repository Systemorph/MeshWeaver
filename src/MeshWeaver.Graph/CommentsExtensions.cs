using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Markdown;
using MeshWeaver.Markdown.Collaboration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// Extension methods for adding comments support to a message hub.
/// Comments are stored per-node as individual JSON files in _Comment/ sub-partition.
/// Each comment file is named by its GUID ID.
/// </summary>
public static class CommentsExtensions
{
    /// <summary>
    /// The sub-partition name where comments are stored.
    /// </summary>
    public const string CommentPartition = "_Comment";

    /// <summary>
    /// Marker type used to detect if comments are enabled in a hub configuration.
    /// </summary>
    public record CommentsEnabled;

    /// <summary>
    /// Registers the Comment type in the type registry at the mesh level.
    /// This must be called during mesh configuration to enable polymorphic JSON deserialization.
    /// </summary>
    public static TBuilder WithCommentType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        // Type registration is now centralized in CommentNodeType.AddCommentType()
        return builder;
    }

    /// <summary>
    /// Adds comments support to the message hub configuration.
    /// This registers the Comment type, adds it to the data source, and enables the Comments view area.
    /// Comments are stored as individual JSON files in _Comment/ sub-partition with GUID filenames.
    /// Call this after AddMeshDataSource() to enable comments for a node type.
    /// </summary>
    public static MessageHubConfiguration AddComments(this MessageHubConfiguration configuration)
    {
        return configuration
            .WithType<CreateCommentRequest>(nameof(CreateCommentRequest))
            .WithType<CreateCommentResponse>(nameof(CreateCommentResponse))
            .Set(new CommentsEnabled())
            .WithHandler<CreateCommentRequest>(HandleCreateCommentRequest)
            .AddData(data => data.WithDataSource(_ =>
                new MeshDataSource(Guid.NewGuid().AsString(), data.Workspace)
                    .WithType<Comment>(CommentPartition, nameof(Comment))))
            .AddLayout(layout => layout
                .WithView(MeshNodeLayoutAreas.CommentsArea, CommentsView.Comments));
    }

    /// <summary>
    /// Handles CreateCommentRequest by anchoring the comment to the document WITHOUT mutating it:
    ///   1. Reads the current node once (for its content + <see cref="MeshNode.Version"/>)
    ///   2. For a text selection, captures the (<see cref="Comment.Start"/>/<see cref="Comment.Length"/>)
    ///      range plus the anchor text the comment is taken against, and records it on the satellite
    ///   3. Creates the Comment MeshNode in the <c>_Comment</c> sub-partition via meshService.CreateNode
    ///   4. For a reply (parent is itself a Comment) appends the reply id to the parent's Replies list
    ///   5. Posts CreateCommentResponse once the node is created
    /// <para>
    /// The document text is never rewritten — the inline highlight is re-derived from the satellite
    /// at render time (see <see cref="CommentRendering"/>). This is what lets a Comment-only user
    /// (no document Update permission) comment, and removes the old "fragment didn't match →
    /// no-op Update hangs forever" failure. Never awaits; never uses persistence directly.
    /// </para>
    /// </summary>
    private static IMessageDelivery HandleCreateCommentRequest(
        IMessageHub hub,
        IMessageDelivery<CreateCommentRequest> request)
    {
        var logger = hub.ServiceProvider.GetService<ILogger<CommentsEnabled>>();
        var msg = request.Message;

        try
        {
            var nodePath = hub.Address.ToString();
            logger?.LogDebug("[CreateComment] START on {Path}, Author={Author}, SelectedText='{SelectedText}'",
                nodePath, msg.Author, msg.SelectedText?.Length > 50 ? msg.SelectedText[..50] + "..." : msg.SelectedText);

            var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
            var workspace = hub.GetWorkspace();

            var markerId = Guid.NewGuid().ToString("N")[..8];
            var author = msg.Author;
            var selectedText = msg.SelectedText;
            var hasTextSelection = !string.IsNullOrWhiteSpace(selectedText);

            // Read the current node once: a reply targets a Comment node, a text/page comment targets
            // a Markdown node. We need its content (to anchor) and Version (to stamp on the comment).
            workspace.GetMeshNodeStream()
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(5))
                .Catch<MeshNode, Exception>(_ => Observable.Return((MeshNode)null!))
                .SelectMany(node =>
                {
                    var isReply = node?.Content is Comment;
                    var version = node?.Version ?? 0;

                    // Capture the selection as a (Start, Length) range in the document's clean text.
                    var start = -1;
                    var length = 0;
                    string? anchorText = null;
                    if (hasTextSelection && !isReply
                        && node?.Content is MarkdownContent mdContent && !string.IsNullOrEmpty(mdContent.Content))
                    {
                        anchorText = MarkdownAnnotationParser.StripAllMarkers(mdContent.Content);
                        (start, length) = CommentRendering.Capture(
                            anchorText, msg.StartFragment, msg.EndFragment, selectedText);
                    }
                    var anchored = start >= 0 && length > 0;

                    var comment = new Comment
                    {
                        Id = markerId,
                        PrimaryNodePath = nodePath,
                        MarkerId = anchored ? markerId : null,
                        HighlightedText = anchored ? selectedText : null,
                        Author = author,
                        Text = msg.CommentText,
                        CreatedAt = DateTimeOffset.UtcNow,
                        Status = CommentStatus.Active,
                        Version = anchored ? version : 0,
                        Start = anchored ? start : -1,
                        Length = anchored ? length : 0,
                        AnchorText = anchored ? anchorText : null
                    };
                    var commentNode = new MeshNode(markerId, $"{nodePath}/{CommentPartition}")
                    {
                        Name = $"Comment by {author}",
                        NodeType = CommentNodeType.NodeType,
                        MainNode = nodePath,
                        Content = comment
                    };

                    logger?.LogInformation(
                        "[CreateComment] Anchoring {Id} on {Path}: anchored={Anchored} pos={Start}+{Length} v={Version}",
                        markerId, nodePath, anchored, start, length, version);

                    var create = meshService.CreateNode(commentNode);

                    // Reply: append the reply id to the parent Comment's Replies list. (Page/text
                    // comments on a Markdown node leave the document untouched — no parent write.)
                    if (isReply)
                        return create.SelectMany(_ => workspace.GetMeshNodeStream().Update(n =>
                            n.Content is Comment parent
                                ? n with { Content = parent with { Replies = parent.Replies.Add(markerId) } }
                                : n));

                    return create;
                })
                .Subscribe(
                    _ => hub.Post(
                        new CreateCommentResponse { Success = true, CommentId = markerId, MarkerId = markerId },
                        o => o.ResponseFor(request)),
                    ex =>
                    {
                        logger?.LogWarning(ex, "[CreateComment] FAILED for {Path}", nodePath);
                        hub.Post(new CreateCommentResponse { Success = false, Error = ex.Message },
                            o => o.ResponseFor(request));
                    });

            return request.Processed();
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "[CreateComment] EXCEPTION on {Path}", hub.Address);
            hub.Post(new CreateCommentResponse { Success = false, Error = ex.Message },
                o => o.ResponseFor(request));
            return request.Processed();
        }
    }

    /// <summary>
    /// Checks if comments are enabled in the configuration.
    /// </summary>
    public static bool HasComments(this MessageHubConfiguration configuration)
        => configuration.Get<CommentsEnabled>() != null;
}

/// <summary>
/// Reusable view implementations for bottom-of-page comments.
/// Uses mesh query to reactively discover child Comment nodes without a MarkerId
/// (i.e. general comments not anchored to specific text in the document).
/// </summary>
public static class CommentsView
{
    private const int InitialCommentCount = 10;
    private const int LoadMoreCount = 10;

    /// <summary>
    /// Renders the Comments area for the node.
    /// Uses IMeshService to find child Comment nodes that have no MarkerId (non-range comments).
    /// Each comment is rendered via its LayoutArea Overview.
    /// </summary>
    public static IObservable<UiControl?> Comments(LayoutAreaHost host, RenderingContext _)
    {
        var nodePath = host.Hub.Address.ToString();
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var currentUser = accessService?.Context?.Name ?? "";

        var permissionsStream = host.Hub.GetEffectivePermissions(nodePath);

        // Reactive comment list via synced query — path-keyed dedup, all-Initial
        // gating, hub-level delete fast-path. Full snapshot per emission; just
        // rebuild the projection.
        var commentsDataId = $"pageComments_{nodePath.Replace("/", "_")}";
        host.UpdateData(commentsDataId, Array.Empty<LayoutAreaControl>());

        host.Workspace.GetQuery(
                $"comments:{nodePath}",
                $"namespace:{nodePath}/{CommentsExtensions.CommentPartition} nodeType:{CommentNodeType.NodeType}")
            .Subscribe(snapshot =>
            {
                var commentControls = snapshot
                    .Where(n => n.Content is Comment)
                    .OrderByDescending(n => n.ContentAs<Comment>(host.Hub.JsonSerializerOptions)!.CreatedAt)
                    .Select(n => Controls.LayoutArea(n.Path, CommentLayoutAreas.OverviewArea))
                    .ToArray();
                host.UpdateData(commentsDataId, commentControls);
            });

        // Combine permissions with comment data to build the view
        return permissionsStream.Select(perms =>
        {
            var canComment = perms.HasFlag(Permission.Comment) || perms.HasFlag(Permission.Update);
            return (UiControl?)BuildCommentsSection(host, nodePath, currentUser, canComment, commentsDataId);
        });
    }

    /// <summary>
    /// Builds an inline comments section for embedding in other views.
    /// Shows a header and embeds the Comments layout area.
    /// </summary>
    public static UiControl BuildInlineCommentsSection(LayoutAreaHost host)
    {
        var section = Controls.Stack.WithWidth("100%").WithStyle("margin-top: 32px; border-top: 1px solid var(--neutral-stroke-rest); padding-top: 16px;");
        section = section.WithView(Controls.LayoutArea(host.Hub.Address, MeshNodeLayoutAreas.CommentsArea).WithShowProgress(false));
        return section;
    }

    private static UiControl BuildCommentsSection(LayoutAreaHost host, string nodePath,
        string currentUser, bool canComment, string commentsDataId)
    {
        var container = Controls.Stack.WithWidth("100%");

        // Header with "Add Comment" button
        var headerRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithWidth("100%")
            .WithStyle("align-items: center; justify-content: space-between; margin-bottom: 8px;");

        headerRow = headerRow.WithView(Controls.Html(
            "<span style=\"font-weight: 600; font-size: 0.9rem;\">Comments</span>"));

        if (canComment && !string.IsNullOrEmpty(currentUser))
        {
            headerRow = headerRow.WithView(BuildAddCommentButton(host, nodePath));
        }

        container = container.WithView(headerRow);

        // Inline comment create form — shown when newCommentPathStateId is non-empty
        var newCommentPathStateId = $"newComment_{nodePath.Replace("/", "_")}";
        container = container.WithView((h, _) =>
        {
            h.UpdateData(newCommentPathStateId, "");
            return h.Stream.GetDataStream<string>(newCommentPathStateId)
                .DistinctUntilChanged()
                .Select(commentPath =>
                {
                    if (string.IsNullOrEmpty(commentPath))
                        return (UiControl)Controls.Stack;

                    return (UiControl)BuildCommentCreateForm(h, commentPath, newCommentPathStateId, nodePath, currentUser);
                });
        });

        // Data-bound comment list with load-more
        var visibleCountStateId = $"commentsVisible_{nodePath.Replace("/", "_")}";

        container = container.WithView((h, _) =>
        {
            h.UpdateData(visibleCountStateId, InitialCommentCount);

            return h.Stream.GetDataStream<LayoutAreaControl[]>(commentsDataId)
                .CombineLatest(
                    h.Stream.GetDataStream<int>(visibleCountStateId),
                    (commentControls, visibleCount) =>
                    {
                        if (commentControls == null || commentControls.Length == 0)
                            return (UiControl)Controls.Html(
                                "<p style=\"color: var(--neutral-foreground-hint); font-style: italic; font-size: 0.85rem;\">No comments yet.</p>");

                        var section = Controls.Stack.WithWidth("100%");

                        var shown = Math.Min(visibleCount, commentControls.Length);
                        for (var i = 0; i < shown; i++)
                        {
                            section = section.WithView(commentControls[i]);
                        }

                        if (shown < commentControls.Length)
                        {
                            var remaining = commentControls.Length - shown;
                            section = section.WithView(Controls.Button(
                                    $"View {Math.Min(remaining, LoadMoreCount)} more comment{(Math.Min(remaining, LoadMoreCount) == 1 ? "" : "s")}")
                                .WithAppearance(Appearance.Lightweight)
                                .WithStyle("margin-top: 8px; font-size: 0.85rem;")
                                .WithClickAction(ctx =>
                                {
                                    ctx.Host.UpdateData(visibleCountStateId, visibleCount + LoadMoreCount);
                                    return Task.CompletedTask;
                                }));
                        }

                        return (UiControl)section;
                    });
        });

        return container;
    }

    /// <summary>
    /// Builds the inline comment creation form with markdown editor, Cancel, and Create buttons.
    /// Matches the Reply pattern from CommentLayoutAreas.
    /// </summary>
    private static UiControl BuildCommentCreateForm(LayoutAreaHost host, string commentPath, string stateId,
        string nodePath, string currentUser)
    {
        var nodeFactory = host.Hub.ServiceProvider.GetRequiredService<IMeshService>();
        var textDataId = $"commentText_{commentPath.Replace("/", "_")}";

        host.UpdateData(textDataId, new Dictionary<string, object?> { ["text"] = "" });

        var stack = Controls.Stack
            .WithStyle("margin-top: 4px; padding: 8px; border: 1px solid var(--neutral-stroke-rest); border-radius: 6px;");

        // Markdown editor bound to local data area
        var editor = new MarkdownEditorControl()
            .WithDocumentId(commentPath)
            .WithHeight("100px")
            .WithPlaceholder("Write your comment...") with
        {
            Value = new JsonPointerReference("text"),
            DataContext = LayoutAreaReference.GetDataPointer(textDataId)
        };
        stack = stack.WithView(editor);

        // Button row: Cancel and Create
        stack = stack.WithView(Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap(8)
            .WithStyle("margin-top: 4px; justify-content: flex-end;")
            .WithView(Controls.Button("Cancel")
                .WithAppearance(Appearance.Neutral)
                .WithClickAction(_ =>
                {
                    // Nothing was persisted — the draft lives only in the form's data stream.
                    // Just close the form; there is no placeholder node to delete.
                    host.UpdateData(stateId, "");
                    return Task.CompletedTask;
                }))
            .WithView(Controls.Button("Create")
                .WithAppearance(Appearance.Accent)
                .WithClickAction(ctx =>
                {
                    // Read the draft text from the form data stream, then write the comment with ONE
                    // CreateNode (Active, through the access-control pipeline). No transient placeholder
                    // and no primary-node clobber — the comment is authored at its own commentPath.
                    ctx.Host.Stream.GetDataStream<Dictionary<string, object?>>(textDataId)
                        .Take(1)
                        .Subscribe(data =>
                        {
                            var text = data?.GetValueOrDefault("text")?.ToString() ?? "";
                            var commentId = commentPath.Split('/').Last();
                            var commentNode = new MeshNode(commentId, $"{nodePath}/{CommentsExtensions.CommentPartition}")
                            {
                                Name = "Comment",
                                NodeType = CommentNodeType.NodeType,
                                MainNode = nodePath,
                                State = MeshNodeState.Active,
                                Content = new Comment
                                {
                                    Id = commentId,
                                    PrimaryNodePath = nodePath,
                                    Author = currentUser,
                                    Status = CommentStatus.Active,
                                    Text = text
                                }
                            };
                            nodeFactory.CreateNode(commentNode).Subscribe(
                                _ => host.UpdateData(stateId, ""),
                                // Keep the draft form OPEN on failure (don't clear stateId) so the user's
                                // text isn't silently lost, and log so the failure is diagnosable.
                                ex => host.Hub.ServiceProvider.GetService<ILogger<LayoutAreaHost>>()
                                    ?.LogWarning(ex, "Failed to create comment at {Path}", commentPath));
                        });
                    return Task.CompletedTask;
                })));

        return stack;
    }

    /// <summary>
    /// Builds the "Add Comment" emoji button. Opens the inline creation form for a fresh comment
    /// path — NOTHING is persisted until the user clicks Create (then a single <c>CreateNode</c>
    /// writes the comment Active). No transient placeholder is written, so an unsent draft never
    /// appears in the comment list and there is no PG-invisible node to resolve.
    /// </summary>
    private static UiControl BuildAddCommentButton(LayoutAreaHost host, string nodePath)
    {
        return Controls.Html("<span style=\"cursor: pointer; font-size: 0.85rem; color: var(--accent-fill-rest);\" title=\"Add Comment\">+ Comment</span>")
            .WithClickAction(_ =>
            {
                var commentId = Guid.NewGuid().AsString();
                var commentPath = $"{nodePath}/{CommentsExtensions.CommentPartition}/{commentId}";
                var newCommentPathStateId = $"newComment_{nodePath.Replace("/", "_")}";
                host.UpdateData(newCommentPathStateId, commentPath);
                return Task.CompletedTask;
            });
    }
}
