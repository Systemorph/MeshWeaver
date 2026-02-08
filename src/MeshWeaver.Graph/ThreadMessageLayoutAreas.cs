using System.Reactive.Linq;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph;

/// <summary>
/// Provides dedicated views for ThreadMessage nodes.
/// Renders based on ThreadMessageType:
/// - EditingPrompt: MarkdownEditorControl for editing
/// - ExecutedInput: MarkdownControl readonly, styled as user message
/// - AgentResponse: MarkdownControl readonly, styled as assistant message
/// </summary>
public static class ThreadMessageLayoutAreas
{
    public const string OverviewArea = "Overview";

    /// <summary>
    /// Adds the thread message views to the hub's layout.
    /// </summary>
    public static MessageHubConfiguration AddThreadMessageViews(this MessageHubConfiguration configuration)
        => configuration
            .AddLayout(layout => layout
                .WithDefaultArea(OverviewArea)
                .WithView(OverviewArea, Overview)
                .WithView(MeshNodeLayoutAreas.ThumbnailArea, Thumbnail));

    /// <summary>
    /// Renders the Overview area for a ThreadMessage node.
    /// Switches on message.Type to return appropriate control.
    /// </summary>
    public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return<MeshNode[]>(Array.Empty<MeshNode>());

        return nodeStream.Select(nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            return BuildMessageView(host, node, hubPath);
        });
    }

    /// <summary>
    /// Builds the appropriate view for a ThreadMessage based on its Type.
    /// </summary>
    private static UiControl BuildMessageView(LayoutAreaHost host, MeshNode? node, string messagePath)
    {
        var message = node?.Content as ThreadMessage;
        if (message == null)
        {
            return Controls.Html("<div style=\"color: var(--neutral-foreground-hint); padding: 8px;\">No message content</div>");
        }

        return message.Type switch
        {
            ThreadMessageType.EditingPrompt => BuildEditingPromptView(host, message, messagePath),
            ThreadMessageType.ExecutedInput => BuildUserMessageView(message),
            ThreadMessageType.AgentResponse => BuildAgentResponseView(message),
            _ => BuildUserMessageView(message) // Default fallback
        };
    }

    /// <summary>
    /// Builds an editing view with MarkdownEditorControl for EditingPrompt messages.
    /// </summary>
    private static UiControl BuildEditingPromptView(LayoutAreaHost host, ThreadMessage message, string messagePath)
    {
        var container = Controls.Stack
            .WithWidth("100%")
            .WithStyle("padding: 12px;");

        // Author header
        var authorName = message.AuthorName ?? "You";
        container = container.WithView(Controls.Html(
            $"<div style=\"font-weight: 600; font-size: 0.85rem; color: var(--accent-fill-rest); margin-bottom: 8px;\">{System.Web.HttpUtility.HtmlEncode(authorName)} (editing)</div>"));

        // MarkdownEditorControl for editing
        var editor = new MarkdownEditorControl()
            .WithDocumentId(messagePath)
            .WithValue(message.Text)
            .WithHeight("200px")
            .WithMaxHeight("300px")
            .WithPlaceholder("Type your message...");

        container = container.WithView(editor);

        return container;
    }

    /// <summary>
    /// Builds a readonly view for ExecutedInput (user) messages.
    /// Styled with accent background for user messages.
    /// </summary>
    private static UiControl BuildUserMessageView(ThreadMessage message)
    {
        var authorName = message.AuthorName ?? "You";

        var container = Controls.Stack
            .WithWidth("100%")
            .WithStyle("display: flex; justify-content: flex-end; margin-bottom: 12px;");

        var bubble = Controls.Stack
            .WithStyle("max-width: 80%; padding: 12px 16px; border-radius: 12px; border-bottom-right-radius: 4px; background: var(--accent-fill-rest); color: white;")
            .WithView(Controls.Html($"<div style=\"font-weight: 600; font-size: 0.85rem; margin-bottom: 4px;\">{System.Web.HttpUtility.HtmlEncode(authorName)}</div>"))
            .WithView(new MarkdownControl(message.Text).WithStyle("color: white;"))
            .WithView(Controls.Html($"<div style=\"font-size: 0.75rem; opacity: 0.7; margin-top: 4px;\">{message.Timestamp:HH:mm}</div>"));

        // Add delegation link if present
        if (!string.IsNullOrEmpty(message.DelegationPath))
        {
            bubble = bubble.WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("margin-top: 8px; padding-top: 8px; border-top: 1px solid rgba(255,255,255,0.2);")
                .WithView(Controls.Icon("ArrowRight").WithStyle("font-size: 14px;"))
                .WithView(new NavLinkControl("View delegation", null, $"/{message.DelegationPath}/Thread")));
        }

        container = container.WithView(bubble);
        return container;
    }

    /// <summary>
    /// Builds a readonly view for AgentResponse (assistant) messages.
    /// Styled with neutral background for assistant messages.
    /// </summary>
    private static UiControl BuildAgentResponseView(ThreadMessage message)
    {
        var authorName = message.AuthorName ?? "Assistant";
        var isSystem = message.Role.Equals("system", StringComparison.OrdinalIgnoreCase);
        var bgColor = isSystem ? "var(--neutral-layer-3)" : "var(--neutral-layer-2)";

        var container = Controls.Stack
            .WithWidth("100%")
            .WithStyle("display: flex; justify-content: flex-start; margin-bottom: 12px;");

        var bubble = Controls.Stack
            .WithStyle($"max-width: 80%; padding: 12px 16px; border-radius: 12px; border-bottom-left-radius: 4px; background: {bgColor}; color: var(--neutral-foreground-rest);")
            .WithView(Controls.Html($"<div style=\"font-weight: 600; font-size: 0.85rem; margin-bottom: 4px;\">{System.Web.HttpUtility.HtmlEncode(authorName)}</div>"))
            .WithView(new MarkdownControl(message.Text))
            .WithView(Controls.Html($"<div style=\"font-size: 0.75rem; opacity: 0.7; margin-top: 4px;\">{message.Timestamp:HH:mm}</div>"));

        // Add delegation link if present
        if (!string.IsNullOrEmpty(message.DelegationPath))
        {
            bubble = bubble.WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("margin-top: 8px; padding-top: 8px; border-top: 1px solid var(--neutral-stroke-rest);")
                .WithView(Controls.Icon("ArrowRight").WithStyle("font-size: 14px;"))
                .WithView(new NavLinkControl("View delegation", null, $"/{message.DelegationPath}/Thread")));
        }

        container = container.WithView(bubble);
        return container;
    }

    /// <summary>
    /// Renders a compact thumbnail for thread message nodes.
    /// </summary>
    public static IObservable<UiControl?> Thumbnail(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return<MeshNode[]>(Array.Empty<MeshNode>());

        return nodeStream.Select(nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            return BuildThumbnail(node);
        });
    }

    private static UiControl BuildThumbnail(MeshNode? node)
    {
        var message = node?.Content as ThreadMessage;
        var role = message?.Role ?? "unknown";
        var text = message?.Text ?? "";
        var preview = text.Length > 50 ? text[..47] + "..." : text;
        var icon = role.Equals("user", StringComparison.OrdinalIgnoreCase) ? "Person" : "Bot";
        var typeLabel = message?.Type switch
        {
            ThreadMessageType.EditingPrompt => "Editing",
            ThreadMessageType.ExecutedInput => "User",
            ThreadMessageType.AgentResponse => "Assistant",
            _ => role
        };

        return Controls.Stack
            .WithStyle("padding: 8px 12px; background: var(--neutral-layer-card-container); border: 1px solid var(--neutral-stroke-rest); border-radius: 6px;")
            .WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("align-items: center; gap: 8px;")
                .WithView(Controls.Icon(icon).WithStyle("font-size: 16px; color: var(--accent-fill-rest);"))
                .WithView(Controls.Html($"<span style=\"font-size: 0.85rem; font-weight: 500;\">{typeLabel}</span>")))
            .WithView(Controls.Html($"<p style=\"margin: 4px 0 0 0; font-size: 0.85rem; color: var(--neutral-foreground-hint); overflow: hidden; text-overflow: ellipsis; white-space: nowrap;\">{System.Web.HttpUtility.HtmlEncode(preview)}</p>"));
    }
}
