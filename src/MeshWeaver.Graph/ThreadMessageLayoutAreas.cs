using System.Reactive.Linq;
using Humanizer;
using MeshWeaver.AI;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Domain;
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
    /// <summary>
    /// Adds the thread message views to the hub's layout.
    /// </summary>
    public static MessageHubConfiguration AddThreadMessageViews(this MessageHubConfiguration configuration)
        => configuration
            .AddLayout(layout => layout
                .WithDefaultArea(ThreadMessageNodeType.OverviewArea)
                .WithView(ThreadMessageNodeType.OverviewArea, Overview)
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
            // Node not yet loaded — show loading skeleton as progress indicator
            return Controls.Html(
                "<div style=\"padding: 12px 16px; margin-bottom: 12px;\">" +
                "<div style=\"height: 14px; width: 80px; background: var(--neutral-stroke-rest); border-radius: 4px; margin-bottom: 8px; animation: agent-skeleton-pulse 1.5s ease-in-out infinite;\"></div>" +
                "<div style=\"height: 14px; width: 60%; background: var(--neutral-stroke-rest); border-radius: 4px; margin-bottom: 6px; animation: agent-skeleton-pulse 1.5s ease-in-out infinite; animation-delay: 0.1s;\"></div>" +
                "<div style=\"height: 14px; width: 40%; background: var(--neutral-stroke-rest); border-radius: 4px; animation: agent-skeleton-pulse 1.5s ease-in-out infinite; animation-delay: 0.2s;\"></div>" +
                "</div>" +
                "<style>@keyframes agent-skeleton-pulse { 0%, 100% { opacity: 0.4; } 50% { opacity: 1; } }</style>");
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
    /// Outer flex container right-aligns; inner bubble has max-width and accent background.
    /// </summary>
    private static UiControl BuildUserMessageView(ThreadMessage message)
    {
        var authorName = message.AuthorName ?? "You";

        var bubble = Controls.Stack
            .WithStyle("max-width: 80%; padding: 12px 16px; border-radius: 12px; border-bottom-right-radius: 4px; background: var(--neutral-layer-4); border-inline-end: 3px solid var(--accent-fill-rest); color: var(--neutral-foreground-rest);")
            .WithView(Controls.Html($"<div style=\"font-weight: 600; font-size: 0.85rem; color: var(--accent-fill-rest); margin-bottom: 4px;\">{System.Web.HttpUtility.HtmlEncode(authorName)}</div>"))
            .WithView(new MarkdownControl(message.Text) { ShowReferences = false }.WithStyle("background: transparent;"))
            .WithView(BuildReferenceChips(message.Text))
            .WithView(Controls.Html($"<div style=\"font-size: 0.75rem; color: var(--neutral-foreground-hint); margin-top: 4px;\">{message.Timestamp.Humanize()}</div>"));

        // Add delegation link if present
        if (!string.IsNullOrEmpty(message.DelegationPath))
        {
            bubble = bubble.WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("margin-top: 8px; padding-top: 8px; border-top: 1px solid var(--neutral-stroke-rest);")
                .WithView(Controls.Icon(FluentIcons.ArrowRight(IconSize.Size16)).WithStyle("font-size: 14px;"))
                .WithView(new NavLinkControl("View delegation", null, $"/{message.DelegationPath}/{ThreadNodeType.ThreadArea}")));
        }

        return Controls.Stack
            .WithWidth("100%")
            .WithStyle("align-items: flex-end; margin-bottom: 12px;")
            .WithView(bubble);
    }

    /// <summary>
    /// Builds compact inline reference chips from @references found in message text.
    /// </summary>
    private static UiControl BuildReferenceChips(string? text)
    {
        var paths = MarkdownReferenceExtractor.GetUniquePaths(text);
        if (paths.Count == 0)
            return Controls.Html("");

        var chipHtml = string.Join(" ", paths.Select(path =>
        {
            var displayName = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
            var encoded = System.Web.HttpUtility.HtmlEncode(displayName);
            var encodedPath = System.Web.HttpUtility.HtmlEncode(path);
            return $"<a href=\"/{encodedPath}\" title=\"{encodedPath}\" style=\"display: inline-flex; align-items: center; gap: 4px; padding: 2px 10px; background: var(--neutral-layer-2); border: 1px solid var(--neutral-stroke-rest); border-radius: 16px; font-size: 0.8rem; color: var(--accent-fill-rest); text-decoration: none; white-space: nowrap;\">" +
                   $"<span style=\"max-width: 120px; overflow: hidden; text-overflow: ellipsis;\">{encoded}</span></a>";
        }));

        return Controls.Html($"<div style=\"display: flex; flex-wrap: wrap; gap: 6px; margin-top: 6px;\">{chipHtml}</div>");
    }

    /// <summary>
    /// Builds a readonly view for AgentResponse (assistant) messages.
    /// Outer flex container left-aligns; inner bubble has max-width and neutral background.
    /// Shows animated dots when text is empty (agent generating).
    /// </summary>
    private static UiControl BuildAgentResponseView(ThreadMessage message)
    {
        var authorName = message.AuthorName ?? message.AgentName ?? "Assistant";
        var isSystem = message.Role.Equals("system", StringComparison.OrdinalIgnoreCase);
        var bgColor = isSystem ? "var(--neutral-layer-3)" : "var(--neutral-layer-2)";

        // Build subtitle with model info
        var subtitle = !string.IsNullOrEmpty(message.ModelName)
            ? $"<div style=\"font-size: 0.75rem; color: var(--neutral-foreground-hint); margin-bottom: 4px;\">{System.Web.HttpUtility.HtmlEncode(message.ModelName)}</div>"
            : "";

        // Show progress indicator when response text is empty (agent is generating)
        UiControl contentView;
        if (string.IsNullOrEmpty(message.Text))
        {
            contentView = Controls.Html(
                "<div style=\"display: flex; align-items: center; gap: 8px; padding: 8px 0;\">" +
                "<span style=\"display: inline-flex; gap: 4px;\">" +
                "<span style=\"width: 6px; height: 6px; border-radius: 50%; background: var(--neutral-foreground-hint); animation: agent-dots-blink 1.4s infinite both; animation-delay: 0s;\"></span>" +
                "<span style=\"width: 6px; height: 6px; border-radius: 50%; background: var(--neutral-foreground-hint); animation: agent-dots-blink 1.4s infinite both; animation-delay: 0.2s;\"></span>" +
                "<span style=\"width: 6px; height: 6px; border-radius: 50%; background: var(--neutral-foreground-hint); animation: agent-dots-blink 1.4s infinite both; animation-delay: 0.4s;\"></span>" +
                "</span>" +
                "<span style=\"font-size: 0.85rem; color: var(--neutral-foreground-hint);\">Generating response...</span>" +
                "</div>" +
                "<style>@keyframes agent-dots-blink { 0%, 80%, 100% { opacity: 0.3; } 40% { opacity: 1; } }</style>");
        }
        else
        {
            contentView = new MarkdownControl(message.Text).WithStyle("background: transparent;");
        }

        var bubble = Controls.Stack
            .WithStyle($"max-width: 80%; padding: 12px 16px; border-radius: 12px; border-bottom-left-radius: 4px; background: {bgColor}; color: var(--neutral-foreground-rest);")
            .WithView(Controls.Html($"<div style=\"font-weight: 600; font-size: 0.85rem; margin-bottom: 4px;\">{System.Web.HttpUtility.HtmlEncode(authorName)}</div>"))
            .WithView(Controls.Html(subtitle))
            .WithView(contentView)
            .WithView(Controls.Html($"<div style=\"font-size: 0.75rem; color: var(--neutral-foreground-hint); margin-top: 4px;\">{message.Timestamp.Humanize()}</div>"));

        // Add delegation link if present
        if (!string.IsNullOrEmpty(message.DelegationPath))
        {
            bubble = bubble.WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("margin-top: 8px; padding-top: 8px; border-top: 1px solid var(--neutral-stroke-rest);")
                .WithView(Controls.Icon(FluentIcons.ArrowRight(IconSize.Size16)).WithStyle("font-size: 14px;"))
                .WithView(new NavLinkControl("View delegation", null, $"/{message.DelegationPath}/{ThreadNodeType.ThreadArea}")));
        }

        return Controls.Stack
            .WithWidth("100%")
            .WithStyle("align-items: flex-start; margin-bottom: 12px;")
            .WithView(bubble);
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
            ThreadMessageType.AgentResponse => message?.AgentName ?? "Assistant",
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
