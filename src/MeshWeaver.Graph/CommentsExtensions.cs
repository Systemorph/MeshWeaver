using System.Reactive.Linq;
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
        builder.ConfigureServices(services =>
        {
            var typeRegistry = services.BuildServiceProvider().GetService<ITypeRegistry>();
            typeRegistry?.WithType(typeof(Comment), nameof(Comment));
            return services;
        });
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
            .WithType<Comment>(nameof(Comment))  // Register Comment in type registry
            .Set(new CommentsEnabled())
            .AddData(data => data.WithDataSource(_ =>
                new MeshDataSource(Guid.NewGuid().AsString(), data.Workspace)
                    .WithType<Comment>(CommentPartition, nameof(Comment))))
            .AddLayout(layout => layout
                .WithView(MeshNodeLayoutAreas.CommentsArea, CommentsView.Comments));
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
    /// Uses IMeshQuery to find child Comment nodes that have no MarkerId (non-range comments).
    /// Each comment is rendered via its LayoutArea Overview.
    /// </summary>
    public static IObservable<UiControl?> Comments(LayoutAreaHost host, RenderingContext _)
    {
        var nodePath = host.Hub.Address.ToString();
        var meshQuery = host.Hub.ServiceProvider.GetService<IMeshQuery>();
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var currentUser = accessService?.Context?.Name ?? "";

        var permissionsStream = Observable.FromAsync(() => PermissionHelper.GetEffectivePermissionsAsync(host.Hub, nodePath));

        // Reactive comment list via mesh query
        var commentsDataId = $"pageComments_{nodePath.Replace("/", "_")}";
        host.UpdateData(commentsDataId, Array.Empty<LayoutAreaControl>());

        if (meshQuery != null)
        {
            meshQuery.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                    $"path:{nodePath} nodeType:{CommentNodeType.NodeType} scope:children"))
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
                    // Filter to non-range comments (no MarkerId) that are active
                    var commentControls = list
                        .Where(n => n.Content is Comment c && string.IsNullOrEmpty(c.MarkerId))
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
        section = section.WithView(Controls.Html("<h3 style=\"margin: 0 0 12px 0;\">Comments</h3>"));
        section = section.WithView(Controls.LayoutArea(host.Hub.Address, MeshNodeLayoutAreas.CommentsArea));
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
        var meshCatalog = host.Hub.ServiceProvider.GetRequiredService<IMeshCatalog>();
        var persistence = host.Hub.ServiceProvider.GetService<IPersistenceService>();
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
                    try { await meshCatalog.DeleteNodeAsync(commentPath); } catch { }
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

                    if (persistence != null)
                    {
                        var node = await persistence.GetNodeAsync(commentPath);
                        if (node != null)
                        {
                            var comment = node.Content as Comment ?? new Comment();
                            var activeNode = node with
                            {
                                State = MeshNodeState.Active,
                                Content = comment with { Text = text }
                            };
                            await persistence.SaveNodeAsync(activeNode);
                        }
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
                var commentPath = $"{nodePath}/{commentId}";
                var newCommentPathStateId = $"newComment_{nodePath.Replace("/", "_")}";

                var commentNode = new MeshNode(commentId, nodePath)
                {
                    Name = "Comment",
                    NodeType = CommentNodeType.NodeType,
                    State = MeshNodeState.Transient,
                    Content = new Comment
                    {
                        Id = commentId,
                        PrimaryNodePath = nodePath,
                        Author = currentUser,
                        Status = CommentStatus.Active
                    }
                };

                var meshCatalog = host.Hub.ServiceProvider.GetRequiredService<IMeshCatalog>();
                await meshCatalog.CreateTransientAsync(commentNode);

                host.UpdateData(newCommentPathStateId, commentPath);
            });
    }
}
