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
/// Overview and Thumbnail views for individual Approval nodes.
/// Registered via ApprovalNodeType's AddApprovalViews().
/// </summary>
public static class ApprovalLayoutAreas
{
    public const string OverviewArea = "Overview";
    public const string ThumbnailArea = "Thumbnail";

    /// <summary>
    /// Registers the Approval-specific views (Overview, Thumbnail).
    /// </summary>
    public static MessageHubConfiguration AddApprovalViews(this MessageHubConfiguration configuration)
        => configuration
            .AddLayout(layout => layout
                .WithDefaultArea(OverviewArea)
                .WithView(OverviewArea, Overview)
                .WithView(ThumbnailArea, Thumbnail)
                .WithView(MeshNodeLayoutAreas.CreateNodeArea, CreateLayoutArea.Create)
                .WithView(MeshNodeLayoutAreas.DeleteArea, DeleteLayoutArea.Delete));

    /// <summary>
    /// Overview for an Approval node. Shows requester, approver, purpose, due date, status.
    /// If the current user is the approver and status is Pending, shows Approve/Reject buttons.
    /// </summary>
    public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var currentUser = accessService?.Context?.ObjectId ?? "";

        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return(Array.Empty<MeshNode>());

        return nodeStream.Select(nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            return (UiControl?)BuildOverview(host, node, currentUser);
        });
    }

    private static UiControl BuildOverview(LayoutAreaHost host, MeshNode? node, string currentUser)
    {
        var container = Controls.Stack.WithWidth("100%").WithStyle(MeshNodeLayoutAreas.GetContainerStyle(host));

        container = container.WithView(MeshNodeLayoutAreas.BuildHeader(host, node, false));

        if (node?.Content is not Approval approval)
        {
            container = container.WithView(Controls.Html(
                "<p style=\"color: var(--neutral-foreground-hint); font-style: italic;\">No approval data.</p>"));
            return container;
        }

        // Approval details
        var details = Controls.Stack.WithWidth("100%").WithStyle("gap: 8px;");

        details = details.WithView(Controls.Html(
            $"<div><strong>Status:</strong> <span style=\"{GetStatusStyle(approval.Status)}\">{approval.Status}</span></div>"));
        details = details.WithView(Controls.Html(
            $"<div><strong>Requester:</strong> {EscapeHtml(approval.Requester)}</div>"));
        details = details.WithView(Controls.Html(
            $"<div><strong>Approver:</strong> {EscapeHtml(approval.Approver)}</div>"));

        if (!string.IsNullOrEmpty(approval.Purpose))
            details = details.WithView(Controls.Html(
                $"<div><strong>Purpose:</strong> {EscapeHtml(approval.Purpose)}</div>"));

        if (approval.DueDate.HasValue)
            details = details.WithView(Controls.Html(
                $"<div><strong>Due:</strong> {approval.DueDate.Value:yyyy-MM-dd}</div>"));

        if (approval.ApprovalDate.HasValue)
            details = details.WithView(Controls.Html(
                $"<div><strong>Decision Date:</strong> {approval.ApprovalDate.Value:yyyy-MM-dd HH:mm}</div>"));

        details = details.WithView(Controls.Html(
            $"<div><strong>Created:</strong> {approval.CreatedAt:yyyy-MM-dd HH:mm}</div>"));

        container = container.WithView(details);

        // Approve / Reject buttons if current user is the approver and status is Pending
        if (approval.Status == ApprovalStatus.Pending &&
            string.Equals(approval.Approver, currentUser, StringComparison.OrdinalIgnoreCase))
        {
            var buttonRow = Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithHorizontalGap(8)
                .WithStyle("margin-top: 16px;");

            buttonRow = buttonRow.WithView(Controls.Button("Approve")
                .WithAppearance(Appearance.Accent)
                .WithClickAction(async ctx =>
                {
                    await UpdateApprovalStatusAsync(ctx.Host, node!, ApprovalStatus.Approved);
                }));

            buttonRow = buttonRow.WithView(Controls.Button("Reject")
                .WithAppearance(Appearance.Neutral)
                .WithClickAction(async ctx =>
                {
                    await UpdateApprovalStatusAsync(ctx.Host, node!, ApprovalStatus.Rejected);
                }));

            container = container.WithView(buttonRow);
        }

        // Link to primary document
        if (!string.IsNullOrEmpty(approval.PrimaryNodePath))
        {
            container = container.WithView(Controls.Html(
                $"<div style=\"margin-top: 12px;\"><a href=\"/{approval.PrimaryNodePath}\">Go to document</a></div>"));
        }

        return container;
    }

    private static async Task UpdateApprovalStatusAsync(LayoutAreaHost host, MeshNode node, ApprovalStatus newStatus)
    {
        var persistence = host.Hub.ServiceProvider.GetService<IPersistenceService>();
        if (persistence == null || node.Content is not Approval approval)
            return;

        var updated = node with
        {
            Content = approval with
            {
                Status = newStatus,
                ApprovalDate = DateTimeOffset.UtcNow
            }
        };
        await persistence.SaveNodeAsync(updated);

        // Create notification for the requester
        var meshCatalog = host.Hub.ServiceProvider.GetService<IMeshCatalog>();
        if (meshCatalog != null)
        {
            var notificationType = newStatus == ApprovalStatus.Approved
                ? NotificationType.ApprovalGiven
                : NotificationType.ApprovalRejected;

            var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
            var currentUser = accessService?.Context?.ObjectId ?? "System";

            await NotificationService.CreateNotificationAsync(
                meshCatalog,
                approval.Requester,
                $"Approval {newStatus}",
                $"Your approval request for \"{approval.Purpose}\" has been {newStatus.ToString().ToLowerInvariant()}.",
                notificationType,
                approval.PrimaryNodePath,
                currentUser);
        }
    }

    /// <summary>
    /// Thumbnail view — compact card with status badge, requester name, due date.
    /// </summary>
    public static UiControl Thumbnail(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return(Array.Empty<MeshNode>());

        return Controls.Stack.WithView((h, c) => nodeStream.Select(nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            if (node?.Content is Approval approval)
            {
                var card = Controls.Stack.WithStyle("padding: 8px; border: 1px solid var(--neutral-stroke-rest); border-radius: 6px; gap: 4px;");
                card = card.WithView(Controls.Html(
                    $"<div style=\"display: flex; align-items: center; gap: 8px;\">" +
                    $"<span style=\"{GetStatusStyle(approval.Status)}; font-size: 0.8rem; padding: 2px 8px; border-radius: 4px;\">{approval.Status}</span>" +
                    $"<span style=\"font-weight: 600;\">{EscapeHtml(approval.Purpose)}</span></div>"));
                card = card.WithView(Controls.Html(
                    $"<div style=\"font-size: 0.85rem; color: var(--neutral-foreground-hint);\">From: {EscapeHtml(approval.Requester)}" +
                    (approval.DueDate.HasValue ? $" · Due: {approval.DueDate.Value:yyyy-MM-dd}" : "") +
                    "</div>"));
                return (UiControl)card;
            }
            return MeshNodeThumbnailControl.FromNode(node, hubPath);
        }));
    }

    private static string GetStatusStyle(ApprovalStatus status) => status switch
    {
        ApprovalStatus.Pending => "color: #b8860b; background: #fff8e1",
        ApprovalStatus.Approved => "color: #2e7d32; background: #e8f5e9",
        ApprovalStatus.Rejected => "color: #c62828; background: #ffebee",
        _ => ""
    };

    private static string EscapeHtml(string? text)
        => System.Net.WebUtility.HtmlEncode(text ?? "");
}
