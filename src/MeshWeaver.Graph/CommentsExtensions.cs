using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Markdown;
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
/// Reusable view implementations for comments.
/// These can be used directly in layouts or embedded in other views.
/// </summary>
public static class CommentsView
{
    private const int CommentsPageSize = 10;

    /// <summary>
    /// Renders the Comments area showing comments for the node (Facebook-style).
    /// Shows 10 most recent with "Load more" functionality and an "Add Comment" button.
    /// Uses GetStream for reactive data binding when Comment is registered as mapped type.
    /// </summary>
    public static IObservable<UiControl?> Comments(LayoutAreaHost host, RenderingContext _)
    {
        var nodePath = host.Hub.Address.ToString();
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var currentUser = accessService?.Context?.Name ?? "";

        var permissionsStream = Observable.FromAsync(() => PermissionHelper.GetEffectivePermissionsAsync(host.Hub, nodePath));

        return host.StreamView<Comment>(
            (comments, h) =>
            {
                // Get permissions synchronously from the cached stream
                var perms = Permission.None;
                permissionsStream.Take(1).Subscribe(p => perms = p);
                var canComment = perms.HasFlag(Permission.Comment) || perms.HasFlag(Permission.Update);
                return BuildFacebookStyleComments(h, comments.ToList(), nodePath, currentUser, canComment);
            },
            "Comments");
    }

    /// <summary>
    /// Builds an inline comments section for embedding in other views.
    /// Shows a header, instructions, and embeds the Comments layout area.
    /// </summary>
    public static UiControl BuildInlineCommentsSection(LayoutAreaHost host)
    {
        var section = Controls.Stack.WithWidth("100%").WithStyle("margin-top: 32px; border-top: 1px solid var(--neutral-stroke-rest); padding-top: 16px;");

        section = section.WithView(Controls.Html("<h3 style=\"margin: 0 0 12px 0;\">Comments</h3>"));

        section = section.WithView(Controls.LayoutArea(host.Hub.Address, MeshNodeLayoutAreas.CommentsArea));

        return section;
    }

    private static UiControl BuildFacebookStyleComments(LayoutAreaHost host, List<Comment> comments, string nodePath,
        string currentUser, bool canComment)
    {
        var container = Controls.Stack.WithWidth("100%");

        // Add Comment button (gated by permission)
        if (canComment && !string.IsNullOrEmpty(currentUser))
        {
            container = container.WithView(BuildAddCommentButton(host, nodePath, currentUser));
        }

        if (comments.Count == 0)
        {
            container = container.WithView(
                Controls.Html("<p style=\"color: var(--neutral-foreground-hint); font-style: italic;\">No comments yet.</p>"));
        }
        else
        {
            var recentComments = comments.OrderByDescending(c => c.CreatedAt).Take(CommentsPageSize).ToList();

            foreach (var comment in recentComments)
            {
                var commentAddress = $"{nodePath}/{comment.Id}";
                container = container.WithView(Controls.LayoutArea(commentAddress, CommentLayoutAreas.OverviewArea));
            }

            if (comments.Count > CommentsPageSize)
            {
                var remainingCount = comments.Count - CommentsPageSize;
                container = container.WithView(
                    Controls.Button($"View {remainingCount} more comment{(remainingCount > 1 ? "s" : "")}")
                        .WithAppearance(Appearance.Lightweight)
                        .WithStyle("margin-top: 8px;"));
            }
        }

        // Inline comment create area — rendered when newCommentPathStateId is non-empty
        var newCommentPathStateId = $"newComment_{nodePath.Replace("/", "_")}";
        container = container.WithView((h, _) =>
        {
            h.UpdateData(newCommentPathStateId, "");
            return h.Stream.GetDataStream<string>(newCommentPathStateId)
                .DistinctUntilChanged()
                .Select(commentPath =>
                {
                    if (string.IsNullOrEmpty(commentPath))
                        return (UiControl)Controls.Stack; // empty placeholder

                    return (UiControl)BuildCommentCreateForm(h, commentPath, newCommentPathStateId);
                });
        });

        return container;
    }

    /// <summary>
    /// Builds the inline comment creation form with markdown editor, Cancel, and Create buttons.
    /// Cancel deletes the transient node and hides the form.
    /// Create sets the comment text, marks the node Active, and hides the form.
    /// </summary>
    private static UiControl BuildCommentCreateForm(LayoutAreaHost host, string commentPath, string stateId)
    {
        var meshCatalog = host.Hub.ServiceProvider.GetRequiredService<IMeshCatalog>();
        var persistence = host.Hub.ServiceProvider.GetService<IPersistenceService>();
        var textDataId = $"commentText_{commentPath.Replace("/", "_")}";

        host.UpdateData(textDataId, new Dictionary<string, object?> { ["text"] = "" });

        var stack = Controls.Stack
            .WithStyle("margin-top: 12px; padding: 12px; border: 1px solid var(--neutral-stroke-rest); border-radius: 8px;");

        // Markdown editor bound to local data area
        var editor = new MarkdownEditorControl()
            .WithDocumentId(commentPath)
            .WithHeight("150px")
            .WithPlaceholder("Write your comment...") with
        {
            Value = new JsonPointerReference("text"),
            DataContext = LayoutAreaReference.GetDataPointer(textDataId)
        };
        stack = stack.WithView(editor);

        // Button row: Cancel and Create
        stack = stack.WithView(Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap(12)
            .WithStyle("margin-top: 8px; justify-content: flex-end;")
            .WithView(Controls.Button("Cancel")
                .WithAppearance(Appearance.Neutral)
                .WithClickAction(async _ =>
                {
                    try { await meshCatalog.DeleteNodeAsync(commentPath); } catch { }
                    host.UpdateData(stateId, "");
                }))
            .WithView(Controls.Button("Create")
                .WithAppearance(Appearance.Accent)
                .WithIconStart(FluentIcons.Add())
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
    /// Builds the "Add Comment" button. Creates a transient comment node with PrimaryNodePath set.
    /// </summary>
    private static UiControl BuildAddCommentButton(LayoutAreaHost host, string nodePath, string currentUser)
    {
        return Controls.Button("Add Comment")
            .WithIconStart(FluentIcons.Comment(IconSize.Size16))
            .WithAppearance(Appearance.Accent)
            .WithStyle("margin-bottom: 12px;")
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

    internal static string FormatTimeAgo(DateTimeOffset dateTime)
    {
        var timeSpan = DateTimeOffset.UtcNow - dateTime;

        if (timeSpan.TotalMinutes < 1)
            return "Just now";
        if (timeSpan.TotalMinutes < 60)
            return $"{(int)timeSpan.TotalMinutes}m ago";
        if (timeSpan.TotalHours < 24)
            return $"{(int)timeSpan.TotalHours}h ago";
        if (timeSpan.TotalDays < 7)
            return $"{(int)timeSpan.TotalDays}d ago";
        if (timeSpan.TotalDays < 30)
            return $"{(int)(timeSpan.TotalDays / 7)}w ago";

        return dateTime.ToString("MMM d, yyyy");
    }
}
