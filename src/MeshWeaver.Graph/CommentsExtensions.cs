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
    /// Handles CreateCommentRequest by:
    ///   1. Reading current markdown content from persistence
    ///   2. Finding selected text and inserting comment markers (if text selection provided)
    ///   3. Creating Comment MeshNode via CreateNodeRequest (fire-and-forget)
    ///   4. Updating markdown content via UpdateNodeRequest (fire-and-forget, pushes to GUI stream)
    ///   5. Returning CreateCommentResponse immediately
    /// </summary>
    /// <summary>
    /// Fully non-blocking handler — follows the same pattern as ThreadExecution.HandleSubmitMessage.
    /// 1) Create comment node via meshService.CreateNode (Observable, fire-and-forget)
    /// 2) In Subscribe callback: update markdown content via workspace.UpdateMeshNode
    /// 3) Post response after operations are dispatched
    /// Never awaits. Never uses persistence directly.
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

            // Generate marker ID
            var markerId = Guid.NewGuid().ToString("N")[..8];
            var author = msg.Author;
            var selectedText = msg.SelectedText;
            var hasTextSelection = !string.IsNullOrWhiteSpace(selectedText);

            // Build the comment node
            var comment = new Comment
            {
                Id = markerId,
                PrimaryNodePath = nodePath,
                MarkerId = hasTextSelection ? markerId : null,
                HighlightedText = hasTextSelection ? selectedText : null,
                Author = author,
                Text = msg.CommentText,
                CreatedAt = DateTimeOffset.UtcNow,
                Status = CommentStatus.Active
            };
            var commentNode = new MeshNode(markerId, $"{nodePath}/{CommentPartition}")
            {
                Name = $"Comment by {author}",
                NodeType = CommentNodeType.NodeType,
                MainNode = nodePath,
                Content = comment
            };

            // Create comment/reply node first, then update parent in onNext callback
            // (same pattern as ThreadExecution: create cells → update parent in callback)
            meshService.CreateNode(commentNode).Subscribe(
                _ =>
                {
                    try
                    {
                        logger?.LogInformation("[CreateComment] Node created: {Id} on {Path}", markerId, nodePath);

                        // Update parent node via workspace stream
                        workspace.UpdateMeshNode(node =>
                        {
                            // Markdown node: inject comment markers using fragment-based matching.
                            // JS sends start/end fragments (first/last few words of the selection).
                            // We find them in the rendered plain text and map back to source positions.
                            if (hasTextSelection && node.Content is MarkdownContent mdContent
                                && !string.IsNullOrEmpty(mdContent.Content))
                            {
                                var rawContent = mdContent.Content;
                                var cleanMarkdown = MarkdownAnnotationParser.StripAllMarkers(rawContent);
                                var annotationMap = MarkdownAnnotationParser.BuildCleanToAnnotatedMap(rawContent);

                                var startFrag = msg.StartFragment;
                                var endFrag = msg.EndFragment;

                                // Find start position using start fragment
                                var cleanStart = !string.IsNullOrEmpty(startFrag)
                                    ? MarkdownSourceMap.FindFragmentInSource(cleanMarkdown, startFrag)
                                    : -1;

                                // Find end position using end fragment (search from after start)
                                var cleanEnd = !string.IsNullOrEmpty(endFrag)
                                    ? MarkdownSourceMap.FindFragmentEndInSource(cleanMarkdown, endFrag,
                                        cleanStart >= 0 ? cleanStart : 0)
                                    : -1;

                                // Fallback: use full selected text via MarkdownSourceMap
                                if (cleanStart < 0 || cleanEnd < 0 || cleanEnd <= cleanStart)
                                {
                                    var (plainText, cleanMap) = MarkdownSourceMap.BuildRenderedToSourceMap(cleanMarkdown);
                                    var idx = plainText.IndexOf(selectedText!, StringComparison.OrdinalIgnoreCase);
                                    if (idx < 0)
                                        return node;

                                    cleanStart = idx < cleanMap.Length ? cleanMap[idx] : cleanMarkdown.Length;
                                    cleanEnd = (idx + selectedText!.Length) < cleanMap.Length
                                        ? cleanMap[idx + selectedText.Length]
                                        : cleanMarkdown.Length;
                                }

                                // Map clean → raw
                                var aStart = cleanStart < annotationMap.Length ? annotationMap[cleanStart] : rawContent.Length;
                                var aEnd = cleanEnd < annotationMap.Length ? annotationMap[cleanEnd] : rawContent.Length;

                                var date = DateTime.Now.ToString("MMM d");
                                var meta = !string.IsNullOrEmpty(author) ? $":{author}:{date}" : "";
                                var openTag = $"<!--comment:{markerId}{meta}-->";
                                var closeTag = $"<!--/comment:{markerId}-->";
                                var newContent = rawContent.Insert(aEnd, closeTag).Insert(aStart, openTag);

                                logger?.LogDebug("[CreateComment] Markers inserted: MarkerId={MarkerId}, Pos={Start}-{End}", markerId, aStart, aEnd);
                                return node with { Content = mdContent with { Content = newContent } };
                            }

                            // Comment node: add reply ID to Replies list (same as Thread.Messages)
                            if (node.Content is Comment parentComment)
                            {
                                return node with
                                {
                                    Content = parentComment with
                                    {
                                        Replies = parentComment.Replies.Add(markerId)
                                    }
                                };
                            }

                            return node;
                        });

                        hub.Post(new CreateCommentResponse { Success = true, CommentId = markerId, MarkerId = markerId },
                            o => o.ResponseFor(request));
                    }
                    catch (Exception ex)
                    {
                        logger?.LogError(ex, "[CreateComment] Error in onNext for {Path}", nodePath);
                        hub.Post(new CreateCommentResponse { Success = false, Error = ex.Message },
                            o => o.ResponseFor(request));
                    }
                },
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
        var meshQuery = host.Hub.ServiceProvider.GetService<IMeshService>();
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var currentUser = accessService?.Context?.Name ?? "";

        var permissionsStream = Observable.FromAsync(() => PermissionHelper.GetEffectivePermissionsAsync(host.Hub, nodePath));

        // Reactive comment list via mesh query
        var commentsDataId = $"pageComments_{nodePath.Replace("/", "_")}";
        host.UpdateData(commentsDataId, Array.Empty<LayoutAreaControl>());

        if (meshQuery != null)
        {
            meshQuery.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                    $"namespace:{nodePath}/{CommentsExtensions.CommentPartition} nodeType:{CommentNodeType.NodeType}"))
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
                .Subscribe(list =>
                {
                    // Show all comments (both range and page-level)
                    var commentControls = list
                        .Where(n => n.Content is Comment)
                        .OrderByDescending(n => ((Comment)n.Content!).CreatedAt)
                        .Select(n => Controls.LayoutArea(n.Path, CommentLayoutAreas.OverviewArea))
                        .ToArray();
                    host.UpdateData(commentsDataId, commentControls);
                });
        }

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
            headerRow = headerRow.WithView(BuildAddCommentButton(host, nodePath, currentUser));
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

                    return (UiControl)BuildCommentCreateForm(h, commentPath, newCommentPathStateId);
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
    private static UiControl BuildCommentCreateForm(LayoutAreaHost host, string commentPath, string stateId)
    {
        var nodeFactory = host.Hub.ServiceProvider.GetRequiredService<IMeshService>();
        var meshQuery = host.Hub.ServiceProvider.GetRequiredService<IMeshService>();
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
                .WithClickAction(async _ =>
                {
                    try { await nodeFactory.DeleteNodeAsync(commentPath); } catch { }
                    host.UpdateData(stateId, "");
                }))
            .WithView(Controls.Button("Create")
                .WithAppearance(Appearance.Accent)
                .WithClickAction(async ctx =>
                {
                    var text = "";
                    ctx.Host.Stream.GetDataStream<Dictionary<string, object?>>(textDataId)
                        .Take(1)
                        .Subscribe(data => text = data?.GetValueOrDefault("text")?.ToString() ?? "");

                    var node = await meshQuery.QueryAsync<MeshNode>($"path:{commentPath}").FirstOrDefaultAsync();
                    if (node != null)
                    {
                        var comment = node.Content as Comment ?? new Comment();
                        var activeNode = node with
                        {
                            State = MeshNodeState.Active,
                            Content = comment with { Text = text }
                        };
                        host.Hub.Post(new UpdateNodeRequest(activeNode));
                    }

                    host.UpdateData(stateId, "");
                })));

        return stack;
    }

    /// <summary>
    /// Builds the "Add Comment" emoji button. Creates a transient comment node (no MarkerId)
    /// and shows the inline creation form.
    /// </summary>
    private static UiControl BuildAddCommentButton(LayoutAreaHost host, string nodePath, string currentUser)
    {
        return Controls.Html("<span style=\"cursor: pointer; font-size: 0.85rem; color: var(--accent-fill-rest);\" title=\"Add Comment\">+ Comment</span>")
            .WithClickAction(async _ =>
            {
                var commentId = Guid.NewGuid().AsString();
                var commentPath = $"{nodePath}/{CommentsExtensions.CommentPartition}/{commentId}";
                var newCommentPathStateId = $"newComment_{nodePath.Replace("/", "_")}";

                var commentNode = new MeshNode(commentId, $"{nodePath}/{CommentsExtensions.CommentPartition}")
                {
                    Name = "Comment",
                    NodeType = CommentNodeType.NodeType,
                    MainNode = nodePath,
                    State = MeshNodeState.Transient,
                    Content = new Comment
                    {
                        Id = commentId,
                        PrimaryNodePath = nodePath,
                        Author = currentUser,
                        Status = CommentStatus.Active
                    }
                };

                var nodeFactory = host.Hub.ServiceProvider.GetRequiredService<IMeshService>();
                await nodeFactory.CreateTransientAsync(commentNode);

                host.UpdateData(newCommentPathStateId, commentPath);
            });
    }
}
