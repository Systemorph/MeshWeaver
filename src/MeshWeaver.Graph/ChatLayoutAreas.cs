using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Provides dedicated views for Chat nodes with a conversation-focused layout.
/// Features:
/// - Chat area: Main view showing chat content and message history
/// - History area: Shows delegation sub-chats as a list
/// - Thumbnail: Compact card for catalog display
/// </summary>
public static class ChatLayoutAreas
{
    public const string ChatArea = "Chat";
    public const string HistoryArea = "History";

    /// <summary>
    /// Adds the chat-specific views to the hub's layout.
    /// Sets Chat as the default area for viewing conversations.
    /// </summary>
    public static MessageHubConfiguration AddChatViews(this MessageHubConfiguration configuration)
        => configuration
            .AddLayout(layout => layout
                .WithDefaultArea(ChatArea)
                .WithView(ChatArea, ChatView)
                .WithView(HistoryArea, HistoryView)
                .WithView(MeshNodeLayoutAreas.SettingsArea, MeshNodeLayoutAreas.Settings)
                .WithView(MeshNodeLayoutAreas.MetadataArea, MeshNodeLayoutAreas.Metadata)
                .WithView(MeshNodeLayoutAreas.ThumbnailArea, Thumbnail));

    /// <summary>
    /// Renders the Chat area showing the conversation content.
    /// Displays the message history stored in the ChatNodeContent.
    /// </summary>
    public static IObservable<UiControl?> ChatView(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        // Get the node from the workspace stream
        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return<MeshNode[]>(Array.Empty<MeshNode>());

        return nodeStream.Select(nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            return BuildChatView(host, node, hubPath);
        });
    }

    private static UiControl BuildChatView(LayoutAreaHost host, MeshNode? node, string chatPath)
    {
        var container = Controls.Stack
            .WithWidth("100%")
            .WithHeight("100%")
            .WithStyle("display: flex; flex-direction: column;");

        // Header with chat title and action menu
        var title = GetChatTitle(node);
        var header = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithWidth("100%")
            .WithStyle("align-items: center; padding: 16px; border-bottom: 1px solid var(--neutral-stroke-rest); flex-shrink: 0;");

        // Back button (navigate to user's chat catalog)
        var userId = GetUserIdFromPath(chatPath);
        var catalogPath = ChatNodeType.GetUserChatsPath(userId);
        header = header.WithView(Controls.Button("")
            .WithIconStart(FluentIcons.ArrowLeft(IconSize.Size16))
            .WithAppearance(Appearance.Stealth)
            .WithNavigateToHref($"/{catalogPath}"));

        // Title
        header = header.WithView(Controls.Html($"<h2 style=\"margin: 0 16px; flex: 1;\">{System.Web.HttpUtility.HtmlEncode(title)}</h2>"));

        // Action menu
        header = header.WithView(BuildChatActionMenu(host, node, chatPath));

        container = container.WithView(header);

        // Chat messages content
        var content = node?.Content as ChatNodeContent;
        var messages = content?.Messages ?? new List<ChatMessageContent>();

        if (messages.Count == 0)
        {
            // Empty state
            container = container.WithView(Controls.Stack
                .WithStyle("flex: 1; display: flex; align-items: center; justify-content: center; padding: 32px;")
                .WithView(Controls.Html(
                    "<div style=\"text-align: center; color: var(--neutral-foreground-hint);\">" +
                    "<div style=\"font-size: 48px; margin-bottom: 16px;\">💬</div>" +
                    "<p style=\"font-size: 1.1rem;\">No messages yet</p>" +
                    "<p style=\"font-size: 0.9rem;\">This chat is empty.</p>" +
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
                messagesContainer = messagesContainer.WithView(BuildMessageBubble(message));
            }

            container = container.WithView(messagesContainer);
        }

        // Metadata footer
        var footer = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithWidth("100%")
            .WithStyle("padding: 12px 16px; border-top: 1px solid var(--neutral-stroke-rest); color: var(--neutral-foreground-hint); font-size: 0.85rem;");

        if (content != null)
        {
            footer = footer.WithView(Controls.Html($"<span>Created: {content.CreatedAt:g}</span>"));
            footer = footer.WithView(Controls.Html("<span style=\"margin: 0 8px;\">•</span>"));
            footer = footer.WithView(Controls.Html($"<span>Last activity: {content.LastActivityAt:g}</span>"));

            if (!string.IsNullOrEmpty(content.ProviderId))
            {
                footer = footer.WithView(Controls.Html("<span style=\"margin: 0 8px;\">•</span>"));
                footer = footer.WithView(Controls.Html($"<span>Model: {content.ProviderId}</span>"));
            }
        }

        container = container.WithView(footer);

        return container;
    }

    private static UiControl BuildMessageBubble(ChatMessageContent message)
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
                .WithView(Controls.Icon("ArrowRight").WithStyle("font-size: 14px;"))
                .WithView(new NavLinkControl("View delegation", null, $"/{message.DelegationPath}/{ChatArea}")));
        }

        messageContainer = messageContainer.WithView(bubble);
        return messageContainer;
    }

    /// <summary>
    /// Renders the History area showing delegation sub-chats as a list.
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

        // Query for child Chat nodes (delegations)
        var childrenStream = Observable.FromAsync(async () =>
        {
            try
            {
                return await meshQuery.QueryAsync<MeshNode>($"path:{hubPath} nodeType:{ChatNodeType.NodeType} scope:children").ToListAsync() as IReadOnlyList<MeshNode>;
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

    private static UiControl BuildHistoryView(LayoutAreaHost host, MeshNode? node, string chatPath, IReadOnlyList<MeshNode> delegations)
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
            .WithNavigateToHref($"/{chatPath}/{ChatArea}"));

        var title = GetChatTitle(node);
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

            foreach (var delegation in delegations.OrderByDescending(d =>
                (d.Content as ChatNodeContent)?.CreatedAt ?? DateTime.MinValue))
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
        var content = delegationNode.Content as ChatNodeContent;
        var title = content?.Title ?? delegationNode.Name ?? "Delegation";
        var timestamp = content?.CreatedAt.ToString("g") ?? "";
        var path = delegationNode.Path ?? "";

        return Controls.Stack
            .WithStyle("padding: 16px; background: var(--neutral-layer-card-container); border: 1px solid var(--neutral-stroke-rest); border-radius: 8px; cursor: pointer;")
            .WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("align-items: center; gap: 8px; margin-bottom: 8px;")
                .WithView(Controls.Icon("Chat").WithStyle("color: var(--accent-fill-rest);"))
                .WithView(Controls.Html($"<strong>{System.Web.HttpUtility.HtmlEncode(title)}</strong>")))
            .WithView(Controls.Html($"<span style=\"font-size: 0.85rem; color: var(--neutral-foreground-hint);\">{timestamp}</span>"))
            .WithView(new NavLinkControl("", null, $"/{path}/{ChatArea}"));
    }

    /// <summary>
    /// Renders a compact thumbnail for chat nodes in catalogs.
    /// Shows title, last activity time, and message preview.
    /// </summary>
    public static IObservable<UiControl?> Thumbnail(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return<MeshNode[]>(Array.Empty<MeshNode>());

        return nodeStream.Select(nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            return BuildThumbnail(node, hubPath);
        });
    }

    private static UiControl BuildThumbnail(MeshNode? node, string hubPath)
    {
        var content = node?.Content as ChatNodeContent;
        var title = content?.Title ?? node?.Name ?? "Chat";
        var lastActivity = content?.LastActivityAt.ToString("g") ?? "";
        var messageCount = content?.Messages?.Count ?? 0;

        // Get preview from last message
        var preview = "";
        if (content?.Messages?.Count > 0)
        {
            var lastMessage = content.Messages.LastOrDefault();
            if (lastMessage != null)
            {
                preview = lastMessage.Text.Length > 60
                    ? lastMessage.Text[..57] + "..."
                    : lastMessage.Text;
            }
        }

        return Controls.Stack
            .WithStyle("padding: 16px; background: var(--neutral-layer-card-container); border: 1px solid var(--neutral-stroke-rest); border-radius: 8px;")
            .WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("align-items: center; gap: 12px; margin-bottom: 8px;")
                .WithView(Controls.Icon("Chat").WithStyle("font-size: 24px; color: var(--accent-fill-rest);"))
                .WithView(Controls.Stack
                    .WithView(Controls.Html($"<strong style=\"display: block;\">{System.Web.HttpUtility.HtmlEncode(title)}</strong>"))
                    .WithView(Controls.Html($"<span style=\"font-size: 0.85rem; color: var(--neutral-foreground-hint);\">{lastActivity}</span>"))))
            .WithView(!string.IsNullOrEmpty(preview)
                ? Controls.Html($"<p style=\"margin: 8px 0 0 0; font-size: 0.9rem; color: var(--neutral-foreground-hint); overflow: hidden; text-overflow: ellipsis; white-space: nowrap;\">{System.Web.HttpUtility.HtmlEncode(preview)}</p>")
                : Controls.Html($"<p style=\"margin: 8px 0 0 0; font-size: 0.9rem; color: var(--neutral-foreground-hint);\">{messageCount} messages</p>"))
            .WithView(new NavLinkControl("", null, $"/{hubPath}/{ChatArea}"));
    }

    /// <summary>
    /// Builds the action menu for chat nodes.
    /// </summary>
    private static UiControl BuildChatActionMenu(LayoutAreaHost host, MeshNode? node, string chatPath)
    {
        var menu = Controls.MenuItem("", FluentIcons.MoreHorizontal(IconSize.Size20))
            .WithAppearance(Appearance.Stealth)
            .WithIconOnly();

        // History option (show delegations)
        menu = menu.WithView(new NavLinkControl("Delegations", FluentIcons.History(IconSize.Size16), $"/{chatPath}/{HistoryArea}"));

        // Metadata option
        menu = menu.WithView(new NavLinkControl("Metadata", FluentIcons.Info(IconSize.Size16), $"/{chatPath}/{MeshNodeLayoutAreas.MetadataArea}"));

        // Settings option
        menu = menu.WithView(new NavLinkControl("Settings", FluentIcons.Settings(IconSize.Size16), $"/{chatPath}/{MeshNodeLayoutAreas.SettingsArea}"));

        return menu;
    }

    /// <summary>
    /// Gets the chat title from node content or falls back to default.
    /// </summary>
    private static string GetChatTitle(MeshNode? node)
    {
        if (node?.Content is ChatNodeContent content && !string.IsNullOrEmpty(content.Title))
            return content.Title;

        if (!string.IsNullOrEmpty(node?.Name))
            return node.Name;

        return "Chat";
    }

    /// <summary>
    /// Extracts user ID from a chat path like "User/userId/Chats/chatId".
    /// </summary>
    private static string GetUserIdFromPath(string path)
    {
        var segments = path.Split('/');
        if (segments.Length >= 2 && segments[0] == "User")
            return segments[1];

        return "anonymous";
    }
}
