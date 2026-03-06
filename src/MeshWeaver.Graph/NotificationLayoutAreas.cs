using System.Reactive.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Overview and Thumbnail views for individual Notification nodes.
/// Registered via NotificationNodeType's AddNotificationViews().
/// </summary>
public static class NotificationLayoutAreas
{
    public const string OverviewArea = "Overview";
    public const string ThumbnailArea = "Thumbnail";

    /// <summary>
    /// Registers the Notification-specific views (Overview, Thumbnail).
    /// </summary>
    public static MessageHubConfiguration AddNotificationViews(this MessageHubConfiguration configuration)
        => configuration
            .AddLayout(layout => layout
                .WithDefaultArea(OverviewArea)
                .WithView(OverviewArea, Overview)
                .WithView(ThumbnailArea, Thumbnail)
                .WithView(MeshNodeLayoutAreas.DeleteArea, DeleteLayoutArea.Delete));

    /// <summary>
    /// Overview for a Notification node.
    /// Shows title, message, timestamp, link to target node, and "Mark as Read" toggle.
    /// </summary>
    public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return(Array.Empty<MeshNode>());

        return nodeStream.Select(nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            return (UiControl?)BuildOverview(host, node);
        });
    }

    private static UiControl BuildOverview(LayoutAreaHost host, MeshNode? node)
    {
        var container = Controls.Stack.WithWidth("100%").WithStyle(MeshNodeLayoutAreas.GetContainerStyle(host));

        container = container.WithView(MeshNodeLayoutAreas.BuildHeader(host, node, false));

        if (node?.Content is not Notification notification)
        {
            container = container.WithView(Controls.Html(
                "<p style=\"color: var(--neutral-foreground-hint); font-style: italic;\">No notification data.</p>"));
            return container;
        }

        var details = Controls.Stack.WithWidth("100%").WithStyle("gap: 8px;");

        // Type badge
        details = details.WithView(Controls.Html(
            $"<div><span style=\"{GetTypeBadgeStyle(notification.NotificationType)}; font-size: 0.8rem; padding: 2px 8px; border-radius: 4px;\">{notification.NotificationType}</span></div>"));

        // Title
        details = details.WithView(Controls.Html(
            $"<h3 style=\"margin: 8px 0 4px 0;\">{EscapeHtml(notification.Title)}</h3>"));

        // Message
        if (!string.IsNullOrEmpty(notification.Message))
            details = details.WithView(Controls.Html(
                $"<p style=\"margin: 0;\">{EscapeHtml(notification.Message)}</p>"));

        // Timestamp
        details = details.WithView(Controls.Html(
            $"<div style=\"font-size: 0.85rem; color: var(--neutral-foreground-hint);\">{notification.CreatedAt:yyyy-MM-dd HH:mm}</div>"));

        // Created by
        if (!string.IsNullOrEmpty(notification.CreatedBy))
            details = details.WithView(Controls.Html(
                $"<div style=\"font-size: 0.85rem; color: var(--neutral-foreground-hint);\">From: {EscapeHtml(notification.CreatedBy)}</div>"));

        container = container.WithView(details);

        // Action buttons
        var buttons = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap(8)
            .WithStyle("margin-top: 12px;");

        // Mark as read/unread toggle
        var readLabel = notification.IsRead ? "Mark as Unread" : "Mark as Read";
        buttons = buttons.WithView(Controls.Button(readLabel)
            .WithAppearance(Appearance.Neutral)
            .WithClickAction(async ctx =>
            {
                if (node != null)
                {
                    var updated = node with
                    {
                        Content = notification with { IsRead = !notification.IsRead }
                    };
                    ctx.Host.Hub.Post(new UpdateNodeRequest(updated));
                }
            }));

        // Link to target node
        if (!string.IsNullOrEmpty(notification.TargetNodePath))
        {
            buttons = buttons.WithView(Controls.Button("Go to Document")
                .WithAppearance(Appearance.Accent)
                .WithClickAction(_ =>
                {
                    host.Hub.ServiceProvider.GetService<INavigationService>()
                        ?.NavigateTo($"/{notification.TargetNodePath}");
                    return Task.CompletedTask;
                }));
        }

        container = container.WithView(buttons);

        return container;
    }

    /// <summary>
    /// Thumbnail — compact notification card with bell icon, title, time, and unread indicator.
    /// </summary>
    public static UiControl Thumbnail(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return(Array.Empty<MeshNode>());

        return Controls.Stack.WithView((h, c) => nodeStream.Select(nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            if (node?.Content is Notification notification)
            {
                var card = Controls.Stack.WithStyle(
                    "padding: 8px; border: 1px solid var(--neutral-stroke-rest); border-radius: 6px; gap: 4px;" +
                    (notification.IsRead ? "" : " border-left: 3px solid var(--accent-fill-rest);"));

                var titleHtml = notification.IsRead
                    ? EscapeHtml(notification.Title)
                    : $"<strong>{EscapeHtml(notification.Title)}</strong>";

                card = card.WithView(Controls.Html(
                    $"<div style=\"display: flex; align-items: center; gap: 8px;\">" +
                    $"<span>{titleHtml}</span></div>"));

                card = card.WithView(Controls.Html(
                    $"<div style=\"font-size: 0.85rem; color: var(--neutral-foreground-hint);\">{notification.CreatedAt:yyyy-MM-dd HH:mm}</div>"));

                return (UiControl)card;
            }
            return MeshNodeThumbnailControl.FromNode(node, hubPath);
        }));
    }

    private static string GetTypeBadgeStyle(NotificationType type) => type switch
    {
        NotificationType.ApprovalRequired => "color: #b8860b; background: #fff8e1",
        NotificationType.ApprovalGiven => "color: #2e7d32; background: #e8f5e9",
        NotificationType.ApprovalRejected => "color: #c62828; background: #ffebee",
        NotificationType.General => "color: #1565c0; background: #e3f2fd",
        _ => ""
    };

    private static string EscapeHtml(string? text)
        => System.Net.WebUtility.HtmlEncode(text ?? "");
}
