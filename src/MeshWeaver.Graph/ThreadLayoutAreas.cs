using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.AI;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Graph;

/// <summary>
/// Provides dedicated views for Thread nodes with a conversation-focused layout.
/// Features:
/// - Thread area: Main view showing thread content and message history
/// - History area: Shows delegation sub-threads as a list
/// - Thumbnail: Compact card for catalog display
/// </summary>
public static class ThreadLayoutAreas
{

    /// <summary>
    /// Adds the thread-specific views to the hub's layout.
    /// Sets Chat as the default area for interactive conversations.
    /// </summary>
    public static MessageHubConfiguration AddThreadViews(this MessageHubConfiguration configuration)
        => configuration
            .AddLayout(layout => layout
                .WithDefaultArea(ThreadNodeType.ChatArea)
                .WithView(ThreadNodeType.ChatArea, ChatView)
                .WithView(ThreadNodeType.ThreadArea, ThreadView)
                .WithView(ThreadNodeType.HistoryArea, HistoryView)
                .WithView(MeshNodeLayoutAreas.CreateNodeArea, CreateView)
                .WithView(MeshNodeLayoutAreas.SettingsArea, SettingsLayoutArea.Settings)
                .WithView(MeshNodeLayoutAreas.MetadataArea, MeshNodeLayoutAreas.Metadata)
                .WithView(MeshNodeLayoutAreas.ThumbnailArea, Thumbnail)
                .WithView(MeshNodeLayoutAreas.ThreadsArea, ThreadsCatalog));

    /// <summary>
    /// Renders the Chat area with an interactive chat interface.
    /// Provides markdown editing with @ completion, reference chips, and streaming responses.
    /// When viewing a thread directly, the context is set to the thread's ParentPath (the main object).
    /// </summary>
    public static IObservable<UiControl?> ChatView(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        // Get the node stream to extract ParentPath from thread content
        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return<MeshNode[]>(Array.Empty<MeshNode>());

        return nodeStream.Select(nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            var threadContent = node?.Content as MeshThread;

            // Use ParentPath as context if available (the main object), otherwise fall back to hubPath
            var contextPath = !string.IsNullOrEmpty(threadContent?.ParentPath)
                ? threadContent.ParentPath
                : hubPath;
            var contextDisplayName = !string.IsNullOrEmpty(threadContent?.ParentPath)
                ? GetContextDisplayName(threadContent.ParentPath)
                : GetThreadTitle(node);

            return (UiControl?)new ThreadChatControl()
                .WithThreadPath(hubPath)
                .WithInitialContext(contextPath)
                .WithInitialContextDisplayName(contextDisplayName);
        });
    }

    private static string GetContextDisplayName(string path)
    {
        // Extract the last segment of the path as display name
        var segments = path.Split('/');
        return segments.Length > 0 ? segments[^1] : path;
    }

    private static MeshNode? GetNodeFromWorkspace(LayoutAreaHost host, string path)
    {
        var nodes = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>());
        // Note: This is a synchronous helper; for reactive updates, use the stream directly
        return null; // Will be resolved by data binding in the view
    }

    /// <summary>
    /// Renders the Create area for Thread nodes.
    /// Auto-creates a thread with generated name and redirects to ChatArea.
    /// Thread nodes are created as direct children: {parentPath}/{threadId}
    /// Requires Update permission on the parent node.
    /// </summary>
    public static IObservable<UiControl?> CreateView(LayoutAreaHost host, RenderingContext _)
    {
        var parentAddress = host.Hub.Address;
        var parentPath = parentAddress.ToString();

        // Auto-create and redirect
        return Observable.FromAsync(async () =>
        {
            // Permission gate: thread creation requires Update permission
            var canEdit = await PermissionHelper.CanEditAsync(host.Hub, parentPath);
            if (!canEdit)
            {
                return (UiControl?)Controls.Html(
                    "<p style=\"color: var(--error-foreground); padding: 24px;\">Access denied: You do not have permission to create threads here.</p>");
            }

            var now = DateTime.UtcNow;
            var nodeId = Guid.NewGuid().AsString();
            var name = $"Thread {now:yyyy-MM-dd HH:mm}";

            var threadContent = new MeshThread
            {
                ParentPath = parentPath
            };

            var threadPath = string.IsNullOrEmpty(parentPath) ? nodeId : $"{parentPath}/{nodeId}";

            var newNode = new MeshNode(threadPath)
            {
                Name = name,
                NodeType = ThreadNodeType.NodeType,
                Content = threadContent
            };

            var meshCatalog = host.Hub.ServiceProvider.GetRequiredService<IMeshCatalog>();
            try
            {
                var createdNode = await meshCatalog.CreateNodeAsync(newNode).ConfigureAwait(false);
                return (UiControl?)new RedirectControl(MeshNodeLayoutAreas.BuildContentUrl(createdNode.Path!, ThreadNodeType.ChatArea));
            }
            catch (Exception ex)
            {
                return (UiControl?)Controls.Html($"<p style=\"color: var(--error-foreground);\">Failed to create thread: {ex.Message}</p>");
            }
        });
    }

    /// <summary>
    /// Overrides the default Threads catalog view to add a "Create Thread" button.
    /// Injected via AddThreadViews configuration.
    /// </summary>
    public static UiControl ThreadsCatalog(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var createUrl = string.IsNullOrEmpty(hubPath)
            ? $"/Create?type={Uri.EscapeDataString(ThreadNodeType.NodeType)}"
            : $"/{hubPath}/Create?type={Uri.EscapeDataString(ThreadNodeType.NodeType)}";

        return Controls.Stack
            .WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("justify-content: flex-end; padding: 0 0 12px 0;")
                .WithView(Controls.Button("Create Thread")
                    .WithAppearance(Appearance.Accent)
                    .WithIconStart(FluentIcons.Add())
                    .WithNavigateToHref(createUrl)))
            .WithView(Controls.MeshSearch
                .WithHiddenQuery($"path:{hubPath} scope:children nodeType:{ThreadNodeType.NodeType}")
                .WithPlaceholder("Search threads...")
                .WithRenderMode(MeshSearchRenderMode.Flat)
                .WithMaxColumns(3));
    }

    /// <summary>
    /// Renders the Thread area showing the conversation content.
    /// Queries child ThreadMessage nodes for message history.
    /// Falls back to legacy inline Messages for backward compatibility.
    /// </summary>
    public static IObservable<UiControl?> ThreadView(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var meshQuery = host.Hub.ServiceProvider.GetService<IMeshQuery>();

        // Get the node from the workspace stream
        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return<MeshNode[]>(Array.Empty<MeshNode>());

        // Query for child ThreadMessage nodes
        var messagesStream = Observable.FromAsync(async () =>
        {
            if (meshQuery == null)
                return Array.Empty<MeshNode>() as IReadOnlyList<MeshNode>;

            try
            {
                return await meshQuery.QueryAsync<MeshNode>(
                    $"path:{hubPath} nodeType:{ThreadMessageNodeType.NodeType} scope:children sort:Timestamp-asc"
                ).ToListAsync() as IReadOnlyList<MeshNode>;
            }
            catch
            {
                return Array.Empty<MeshNode>() as IReadOnlyList<MeshNode>;
            }
        });

        return nodeStream.CombineLatest(messagesStream, (nodes, messageNodes) =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            return BuildThreadView(host, node, hubPath, messageNodes ?? Array.Empty<MeshNode>());
        });
    }

    private static UiControl BuildThreadView(LayoutAreaHost host, MeshNode? node, string threadPath, IReadOnlyList<MeshNode> messageNodes)
    {
        var container = Controls.Stack
            .WithWidth("100%")
            .WithHeight("100%")
            .WithStyle("display: flex; flex-direction: column;");

        // Header with thread title and action menu
        var title = GetThreadTitle(node);
        var header = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithWidth("100%")
            .WithStyle("align-items: center; padding: 16px; border-bottom: 1px solid var(--neutral-stroke-rest); flex-shrink: 0;");

        // Back button (navigate to parent context from Thread's ParentPath)
        var content = node?.Content as MeshThread;
        var parentPath = content?.ParentPath;
        var backHref = string.IsNullOrEmpty(parentPath) ? "/" : $"/{parentPath}";
        header = header.WithView(Controls.Button("")
            .WithIconStart(FluentIcons.ArrowLeft(IconSize.Size16))
            .WithAppearance(Appearance.Stealth)
            .WithNavigateToHref(backHref));

        // Title
        header = header.WithView(Controls.Html($"<h2 style=\"margin: 0 16px; flex: 1;\">{System.Web.HttpUtility.HtmlEncode(title)}</h2>"));

        // Action menu
        header = header.WithView(BuildThreadActionMenu(host, node, threadPath));

        container = container.WithView(header);

        // Extract messages from child nodes, sorted by timestamp
        var messages = messageNodes
            .Select(n => n.Content as ThreadMessage)
            .Where(m => m != null && m.Type != ThreadMessageType.EditingPrompt) // Exclude editing prompts
            .OrderBy(m => m!.Timestamp)
            .ToList();

        // Fall back to legacy inline messages if no child nodes
#pragma warning disable CS0618 // Type or member is obsolete
        if (messages.Count == 0 && content?.Messages?.Count > 0)
        {
            messages = content.Messages
                .Where(m => m.Type != ThreadMessageType.EditingPrompt)
                .OrderBy(m => m.Timestamp)
                .ToList()!;
        }
#pragma warning restore CS0618

        if (messages.Count == 0)
        {
            // Empty state
            container = container.WithView(Controls.Stack
                .WithStyle("flex: 1; display: flex; align-items: center; justify-content: center; padding: 32px;")
                .WithView(Controls.Html(
                    "<div style=\"text-align: center; color: var(--neutral-foreground-hint);\">" +
                    "<div style=\"font-size: 48px; margin-bottom: 16px;\">💬</div>" +
                    "<p style=\"font-size: 1.1rem;\">No messages yet</p>" +
                    "<p style=\"font-size: 0.9rem;\">This thread is empty.</p>" +
                    "</div>")));
        }
        else
        {
            // Messages list
            var messagesContainer = Controls.Stack
                .WithWidth("100%")
                .WithStyle("flex: 1; overflow-y: auto; padding: 16px;");

            foreach (var message in messages)
            {
                messagesContainer = messagesContainer.WithView(BuildMessageBubble(message!));
            }

            container = container.WithView(messagesContainer);
        }

        // Metadata footer
        var footer = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithWidth("100%")
            .WithStyle("padding: 12px 16px; border-top: 1px solid var(--neutral-stroke-rest); color: var(--neutral-foreground-hint); font-size: 0.85rem;");

        if (node != null)
        {
            footer = footer.WithView(Controls.Html($"<span>Last activity: {node.LastModified:g}</span>"));
        }

        container = container.WithView(footer);

        return container;
    }

    private static UiControl BuildMessageBubble(ThreadMessage message)
    {
        var isUser = message.Role.Equals("user", StringComparison.OrdinalIgnoreCase);
        var isSystem = message.Role.Equals("system", StringComparison.OrdinalIgnoreCase);

        var alignment = isUser ? "flex-end" : "flex-start";
        var bgColor = isUser
            ? "var(--accent-fill-rest)"
            : isSystem
                ? "var(--neutral-layer-3)"
                : "var(--neutral-layer-2)";
        var textColor = isUser ? "white" : "var(--neutral-foreground-rest)";

        var authorName = message.AuthorName ?? (isUser ? "You" : "Assistant");

        var bubbleStyle = $"max-width: 80%; padding: 12px 16px; border-radius: 12px; background: {bgColor}; color: {textColor};";
        if (isUser)
        {
            bubbleStyle += " border-bottom-right-radius: 4px;";
        }
        else
        {
            bubbleStyle += " border-bottom-left-radius: 4px;";
        }

        var messageContainer = Controls.Stack
            .WithWidth("100%")
            .WithStyle($"display: flex; justify-content: {alignment}; margin-bottom: 12px;");

        var bubble = Controls.Stack
            .WithStyle(bubbleStyle)
            .WithView(Controls.Html($"<div style=\"font-weight: 600; font-size: 0.85rem; margin-bottom: 4px;\">{System.Web.HttpUtility.HtmlEncode(authorName)}</div>"))
            .WithView(Controls.Html($"<div style=\"white-space: pre-wrap;\">{System.Web.HttpUtility.HtmlEncode(message.Text)}</div>"))
            .WithView(Controls.Html($"<div style=\"font-size: 0.75rem; opacity: 0.7; margin-top: 4px;\">{message.Timestamp:HH:mm}</div>"));

        // Add delegation link if present
        if (!string.IsNullOrEmpty(message.DelegationPath))
        {
            bubble = bubble.WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("margin-top: 8px; padding-top: 8px; border-top: 1px solid rgba(255,255,255,0.2);")
                .WithView(Controls.Icon(FluentIcons.ArrowRight(IconSize.Size16)).WithStyle("font-size: 14px;"))
                .WithView(new NavLinkControl("View delegation", null, $"/{message.DelegationPath}/{ThreadNodeType.ThreadArea}")));
        }

        messageContainer = messageContainer.WithView(bubble);
        return messageContainer;
    }

    /// <summary>
    /// Renders the History area showing delegation sub-threads as a list.
    /// </summary>
    public static IObservable<UiControl?> HistoryView(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var meshQuery = host.Hub.ServiceProvider.GetService<IMeshQuery>();

        if (meshQuery == null)
        {
            return Observable.Return<UiControl?>(Controls.Html("<p style=\"color: var(--neutral-foreground-hint);\">Query service not available.</p>"));
        }

        // Get the node from the workspace stream
        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return<MeshNode[]>(Array.Empty<MeshNode>());

        // Query for child Thread nodes (delegations)
        var childrenStream = Observable.FromAsync(async () =>
        {
            try
            {
                return await meshQuery.QueryAsync<MeshNode>($"path:{hubPath} nodeType:{ThreadNodeType.NodeType} scope:children").ToListAsync() as IReadOnlyList<MeshNode>;
            }
            catch
            {
                return Array.Empty<MeshNode>() as IReadOnlyList<MeshNode>;
            }
        });

        return nodeStream.CombineLatest(childrenStream, (nodes, children) =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            return BuildHistoryView(host, node, hubPath, children ?? Array.Empty<MeshNode>());
        });
    }

    private static UiControl BuildHistoryView(LayoutAreaHost host, MeshNode? node, string threadPath, IReadOnlyList<MeshNode> delegations)
    {
        var container = Controls.Stack
            .WithWidth("100%")
            .WithStyle("padding: 24px;");

        // Header with back button
        var header = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithWidth("100%")
            .WithStyle("align-items: center; margin-bottom: 24px;");

        header = header.WithView(Controls.Button("")
            .WithIconStart(FluentIcons.ArrowLeft(IconSize.Size16))
            .WithAppearance(Appearance.Stealth)
            .WithNavigateToHref($"/{threadPath}/{ThreadNodeType.ThreadArea}"));

        var title = GetThreadTitle(node);
        header = header.WithView(Controls.Html($"<h2 style=\"margin: 0 16px;\">Delegations - {System.Web.HttpUtility.HtmlEncode(title)}</h2>"));

        container = container.WithView(header);

        // Delegations list
        if (delegations.Count == 0)
        {
            container = container.WithView(Controls.Html(
                "<div style=\"padding: 32px; text-align: center; color: var(--neutral-foreground-hint);\">" +
                "<p>No delegations yet.</p>" +
                "<p style=\"font-size: 0.9rem;\">When an agent delegates work to another agent, it will appear here.</p>" +
                "</div>"));
        }
        else
        {
            var grid = Controls.LayoutGrid.WithSkin(s => s.WithSpacing(2));

            foreach (var delegation in delegations.OrderByDescending(d => d.LastModified))
            {
                grid = grid.WithView(
                    BuildDelegationCard(delegation),
                    itemSkin => itemSkin.WithXs(12).WithSm(6).WithMd(4).WithLg(4));
            }

            container = container.WithView(grid);
        }

        return container;
    }

    private static UiControl BuildDelegationCard(MeshNode delegationNode)
    {
        var title = delegationNode.Name ?? "Delegation";
        var timestamp = delegationNode.LastModified.ToString("g");
        var path = delegationNode.Path ?? "";

        return Controls.Stack
            .WithStyle("padding: 16px; background: var(--neutral-layer-card-container); border: 1px solid var(--neutral-stroke-rest); border-radius: 8px; cursor: pointer;")
            .WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("align-items: center; gap: 8px; margin-bottom: 8px;")
                .WithView(Controls.Icon(FluentIcons.Chat(IconSize.Size20)).WithStyle("color: var(--accent-fill-rest);"))
                .WithView(Controls.Html($"<strong>{System.Web.HttpUtility.HtmlEncode(title)}</strong>")))
            .WithView(Controls.Html($"<span style=\"font-size: 0.85rem; color: var(--neutral-foreground-hint);\">{timestamp}</span>"))
            .WithView(new NavLinkControl("", null, $"/{path}/{ThreadNodeType.ThreadArea}"));
    }

    /// <summary>
    /// Renders a compact thumbnail for thread nodes in catalogs.
    /// Shows title, last activity time, and message preview.
    /// Queries child ThreadMessage nodes for message count and preview.
    /// </summary>
    public static IObservable<UiControl?> Thumbnail(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var meshQuery = host.Hub.ServiceProvider.GetService<IMeshQuery>();

        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return<MeshNode[]>(Array.Empty<MeshNode>());

        // Query for child ThreadMessage nodes
        var messagesStream = Observable.FromAsync(async () =>
        {
            if (meshQuery == null)
                return Array.Empty<MeshNode>() as IReadOnlyList<MeshNode>;

            try
            {
                return await meshQuery.QueryAsync<MeshNode>(
                    $"path:{hubPath} nodeType:{ThreadMessageNodeType.NodeType} scope:children sort:Timestamp-asc"
                ).ToListAsync() as IReadOnlyList<MeshNode>;
            }
            catch
            {
                return Array.Empty<MeshNode>() as IReadOnlyList<MeshNode>;
            }
        });

        return nodeStream.CombineLatest(messagesStream, (nodes, messageNodes) =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            return BuildThumbnail(node, hubPath, messageNodes ?? Array.Empty<MeshNode>());
        });
    }

    private static UiControl BuildThumbnail(MeshNode? node, string hubPath, IReadOnlyList<MeshNode> messageNodes)
    {
        var content = node?.Content as MeshThread;
        var title = node?.Name ?? "Thread";
        var lastActivity = node?.LastModified.ToString("g") ?? "";

        // Extract messages from child nodes
        var messages = messageNodes
            .Select(n => n.Content as ThreadMessage)
            .Where(m => m != null && m.Type != ThreadMessageType.EditingPrompt)
            .OrderBy(m => m!.Timestamp)
            .ToList();

        // Fall back to legacy inline messages
#pragma warning disable CS0618 // Type or member is obsolete
        if (messages.Count == 0 && content?.Messages?.Count > 0)
        {
            messages = content.Messages
                .Where(m => m.Type != ThreadMessageType.EditingPrompt)
                .ToList()!;
        }
#pragma warning restore CS0618

        var messageCount = messages.Count;

        // Get preview from last message
        var preview = "";
        var lastMessage = messages.LastOrDefault();
        if (lastMessage != null)
        {
            preview = lastMessage.Text.Length > 60
                ? lastMessage.Text[..57] + "..."
                : lastMessage.Text;
        }

        return Controls.Stack
            .WithStyle("padding: 16px; background: var(--neutral-layer-card-container); border: 1px solid var(--neutral-stroke-rest); border-radius: 8px;")
            .WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("align-items: center; gap: 12px; margin-bottom: 8px;")
                .WithView(Controls.Icon(FluentIcons.Chat(IconSize.Size24)).WithStyle("color: var(--accent-fill-rest);"))
                .WithView(Controls.Stack
                    .WithView(Controls.Html($"<strong style=\"display: block;\">{System.Web.HttpUtility.HtmlEncode(title)}</strong>"))
                    .WithView(Controls.Html($"<span style=\"font-size: 0.85rem; color: var(--neutral-foreground-hint);\">{lastActivity}</span>"))))
            .WithView(!string.IsNullOrEmpty(preview)
                ? Controls.Html($"<p style=\"margin: 8px 0 0 0; font-size: 0.9rem; color: var(--neutral-foreground-hint); overflow: hidden; text-overflow: ellipsis; white-space: nowrap;\">{System.Web.HttpUtility.HtmlEncode(preview)}</p>")
                : Controls.Html($"<p style=\"margin: 8px 0 0 0; font-size: 0.9rem; color: var(--neutral-foreground-hint);\">{messageCount} messages</p>"))
            .WithView(new NavLinkControl("", null, $"/{hubPath}/{ThreadNodeType.ChatArea}"));
    }

    /// <summary>
    /// Builds the action menu for thread nodes.
    /// </summary>
    private static UiControl BuildThreadActionMenu(LayoutAreaHost host, MeshNode? node, string threadPath, bool canEdit = true)
    {
        var menu = Controls.MenuItem("", FluentIcons.MoreHorizontal(IconSize.Size20))
            .WithAppearance(Appearance.Stealth)
            .WithIconOnly();

        // Chat option (interactive chat view)
        menu = menu.WithView(new NavLinkControl("Chat", FluentIcons.Chat(IconSize.Size16), $"/{threadPath}/{ThreadNodeType.ChatArea}"));

        // Thread option (read-only message history)
        menu = menu.WithView(new NavLinkControl("Messages", FluentIcons.ChatMultiple(IconSize.Size16), $"/{threadPath}/{ThreadNodeType.ThreadArea}"));

        // History option (show delegations)
        menu = menu.WithView(new NavLinkControl("Delegations", FluentIcons.History(IconSize.Size16), $"/{threadPath}/{ThreadNodeType.HistoryArea}"));

        // Metadata option
        menu = menu.WithView(new NavLinkControl("Metadata", FluentIcons.Info(IconSize.Size16), $"/{threadPath}/{MeshNodeLayoutAreas.MetadataArea}"));

        // Settings option (only when user can edit)
        if (canEdit)
        {
            menu = menu.WithView(new NavLinkControl("Settings", FluentIcons.Settings(IconSize.Size16), $"/{threadPath}/{MeshNodeLayoutAreas.SettingsArea}"));
        }

        return menu;
    }

    /// <summary>
    /// Gets the thread title from node name or falls back to default.
    /// </summary>
    private static string GetThreadTitle(MeshNode? node)
        => !string.IsNullOrEmpty(node?.Name) ? node.Name : "Thread";
}
