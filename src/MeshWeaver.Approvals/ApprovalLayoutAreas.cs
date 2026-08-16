using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Approvals;

/// <summary>
/// Overview and Thumbnail views for individual Approval nodes.
/// Registered via ApprovalNodeType's AddApprovalViews(). Ships with the Approvals module:
/// delisting it removes these views mesh-wide while approval data stays platform-level.
/// </summary>
public static class ApprovalLayoutAreas
{
    /// <summary>Area name for the Overview layout area.</summary>
    public const string OverviewArea = "Overview";
    /// <summary>Area name for the Thumbnail layout area.</summary>
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
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var currentUser = accessService?.Context?.ObjectId ?? "";

        return host.Workspace.GetMeshNodeStream()
            .Select(node => (UiControl?)BuildOverview(host, node, currentUser));
    }

    private static UiControl BuildOverview(LayoutAreaHost host, MeshNode? node, string currentUser)
    {
        var container = Controls.Stack.WithWidth("100%").WithStyle(MeshNodeLayoutAreas.GetContainerStyle(host));

        container = container.WithView(MeshNodeLayoutAreas.BuildHeader(host, node, false));

        var approval = node?.ContentAs<Approval>(host.Hub.JsonSerializerOptions);
        if (approval is null)
        {
            container = container.WithView(Controls.Body(host.Localize("approval.noData"))
                .WithStyle("color: var(--neutral-foreground-hint); font-style: italic;"));
            return container;
        }

        // Approval details — typed label rows, never hand-built HTML strings.
        var details = Controls.Stack.WithWidth("100%").WithStyle("gap: 8px;");

        details = details.WithView(DetailRow(host,
            "approval.status", StatusBadge(host, approval.Status)));
        details = details.WithView(DetailRow(host,
            "approval.requester", Controls.Body(approval.Requester)));
        details = details.WithView(DetailRow(host,
            "approval.approver", Controls.Body(approval.Approver)));

        if (!string.IsNullOrEmpty(approval.Purpose))
            details = details.WithView(DetailRow(host,
                "approval.purpose", Controls.Body(approval.Purpose)));

        if (approval.DueDate.HasValue)
            details = details.WithView(DetailRow(host,
                "approval.due", Controls.Body($"{approval.DueDate.Value:yyyy-MM-dd}")));

        if (approval.ApprovalDate.HasValue)
            details = details.WithView(DetailRow(host,
                "approval.decisionDate", Controls.Body($"{approval.ApprovalDate.Value:yyyy-MM-dd HH:mm}")));

        details = details.WithView(DetailRow(host,
            "approval.created", Controls.Body($"{approval.CreatedAt:yyyy-MM-dd HH:mm}")));

        container = container.WithView(details);

        // Approve / Reject buttons if current user is the approver and status is Pending
        if (approval.Status == ApprovalStatus.Pending &&
            string.Equals(approval.Approver, currentUser, StringComparison.OrdinalIgnoreCase))
        {
            var buttonRow = Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithHorizontalGap(8)
                .WithStyle("margin-top: 16px;");

            buttonRow = buttonRow.WithView(Controls.Button(host.Localize("ui.approve"))
                .WithAppearance(Appearance.Accent)
                .WithClickAction(ctx =>
                {
                    UpdateApprovalStatus(ctx.Host, node!, ApprovalStatus.Approved);
                    return Task.CompletedTask;
                }));

            buttonRow = buttonRow.WithView(Controls.Button(host.Localize("ui.reject"))
                .WithAppearance(Appearance.Neutral)
                .WithClickAction(ctx =>
                {
                    UpdateApprovalStatus(ctx.Host, node!, ApprovalStatus.Rejected);
                    return Task.CompletedTask;
                }));

            container = container.WithView(buttonRow);
        }

        // Link to primary document
        if (!string.IsNullOrEmpty(approval.PrimaryNodePath))
        {
            container = container.WithView(
                Controls.NavLink(host.Localize("ui.goToDocument"), $"/{approval.PrimaryNodePath}")
                    .WithStyle("margin-top: 12px;"));
        }

        return container;
    }

    private static void UpdateApprovalStatus(LayoutAreaHost host, MeshNode node, ApprovalStatus newStatus)
    {
        var approval = node.ContentAs<Approval>(host.Hub.JsonSerializerOptions);
        if (approval is null)
            return;

        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var currentUser = accessService?.Context?.ObjectId ?? "System";
        var currentUserName = accessService?.Context?.Name ?? currentUser;

        if (node.Path is { Length: > 0 } approvalPath)
        {
            var cache = host.Hub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
            cache.Update(approvalPath, n =>
            {
                var a = n.ContentAs<Approval>(host.Hub.JsonSerializerOptions) ?? approval;
                return n with
                {
                    Content = a with
                    {
                        Status = newStatus,
                        ApprovalDate = DateTimeOffset.UtcNow
                    }
                };
            }, host.Hub.JsonSerializerOptions).Subscribe(
                _ => { },
                ex => host.Hub.ServiceProvider.GetService<ILoggerFactory>()
                    ?.CreateLogger(typeof(ApprovalLayoutAreas))
                    .LogWarning(ex, "Approval status write failed for {Path}", approvalPath));
        }

        // Activity + notification — chain as Observables, no await in a click handler.
        var nodeFactory = host.Hub.ServiceProvider.GetRequiredService<IMeshService>();

        IObservable<MeshNode> activityWrite = Observable.Empty<MeshNode>();
        if (!string.IsNullOrEmpty(approval.PrimaryNodePath))
        {
            var verb = newStatus == ApprovalStatus.Approved ? "Approved" : "Rejected";
            var log = new ActivityLog("Approval")
            {
                Start = DateTime.UtcNow,
                End = DateTime.UtcNow,
                Status = ActivityStatus.Succeeded,
                User = new UserInfo(currentUser, currentUserName),
                HubPath = approval.PrimaryNodePath,
            }.Append(new LogMessage($"{verb}: {approval.Purpose}", LogLevel.Information));
            // 🚨 `{owner}/_Activity/{id}` with NodeType "Activity" — the shape every other activity
            // writer uses. The old `{owner}/ActivityLog/{id}` + NodeType "ActivityLog" diverged on
            // BOTH axes and cost the approval activity everything that hangs off them: `_Activity` is
            // a satellite segment routed to the partition's `activities` table, so a plain
            // `ActivityLog` segment landed as ordinary content in the owner's own table; the running-
            // activities stripe queries `namespace:…/_Activity nodeType:Activity`, which matched
            // neither clause; and ActivityNodeType's Overview/Progress views are bound to "Activity",
            // so the node rendered with no activity UI at all.
            var activityNode = MeshNode.FromPath($"{approval.PrimaryNodePath}/_Activity/{log.Id}") with
            {
                NodeType = ActivityNodeType.NodeType,
                MainNode = approval.PrimaryNodePath,
                Name = $"Approval: {verb}",
                State = MeshNodeState.Active,
                Content = log
            };
            activityWrite = nodeFactory.CreateNode(activityNode);
        }

        var notificationType = newStatus == ApprovalStatus.Approved
            ? NotificationType.ApprovalGiven
            : NotificationType.ApprovalRejected;

        activityWrite
            .DefaultIfEmpty()
            .SelectMany(_ => NotificationService.Dispatch(
                host.Hub,
                recipient: approval.Requester,
                mainNodePath: approval.Requester,
                title: $"Approval {newStatus}",
                message: $"Your approval request for \"{approval.Purpose}\" has been {newStatus.ToString().ToLowerInvariant()}.",
                type: notificationType,
                targetNodePath: approval.PrimaryNodePath,
                createdBy: currentUser))
            .Subscribe(
                _ => { /* fire-and-forget success */ },
                _ => { /* errors already logged by hub */ });
    }

    /// <summary>
    /// Thumbnail view — compact card with status badge, requester name, due date.
    /// </summary>
    public static UiControl Thumbnail(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var currentUser = accessService?.Context?.ObjectId ?? "";

        return Controls.Stack.WithView((h, c) => host.Workspace.GetMeshNodeStream()
            .Select(node =>
        {
            var approval = node?.ContentAs<Approval>(host.Hub.JsonSerializerOptions);
            if (approval is not null)
            {
                var card = Controls.Stack.WithStyle("padding: 8px; border: 1px solid var(--neutral-stroke-rest); border-radius: 6px; gap: 4px;");
                card = card.WithView(Controls.Stack
                    .WithOrientation(Orientation.Horizontal)
                    .WithHorizontalGap(8)
                    .WithStyle("align-items: center;")
                    .WithView(StatusBadge(host, approval.Status))
                    .WithView(Controls.Body(approval.Purpose).WithStyle("font-weight: 600;")));

                if (approval.Status == ApprovalStatus.Approved || approval.Status == ApprovalStatus.Rejected)
                {
                    var byLine = approval.Status == ApprovalStatus.Approved
                        ? host.Localize("approval.approvedBy")
                        : host.Localize("approval.rejectedBy");
                    card = card.WithView(HintLine(
                        $"{byLine}: {approval.Approver}" +
                        (approval.ApprovalDate.HasValue ? $" · {approval.ApprovalDate.Value:yyyy-MM-dd}" : "")));
                }
                else
                {
                    card = card.WithView(HintLine(
                        $"{host.Localize("approval.from")}: {approval.Requester}" +
                        (approval.DueDate.HasValue
                            ? $" · {host.Localize("approval.due")}: {approval.DueDate.Value:yyyy-MM-dd}"
                            : "")));

                    if (string.Equals(approval.Approver, currentUser, StringComparison.OrdinalIgnoreCase))
                    {
                        card = card.WithView(Controls.Button(host.Localize("ui.approve"))
                            .WithAppearance(Appearance.Accent)
                            .WithClickAction(ctx =>
                            {
                                UpdateApprovalStatus(ctx.Host, node!, ApprovalStatus.Approved);
                                return Task.CompletedTask;
                            }));
                    }
                }

                return (UiControl)card;
            }
            return MeshNodeThumbnailControl.FromNode(node, hubPath);
        }));
    }

    /// <summary>Bold label + value on one line — the typed replacement for the old HTML strings.</summary>
    private static UiControl DetailRow(LayoutAreaHost host, string labelKey, UiControl value)
        => Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap(6)
            .WithView(Controls.Body($"{host.Localize(labelKey)}:").WithStyle("font-weight: 600;"))
            .WithView(value);

    private static UiControl HintLine(string text)
        => Controls.Body(text).WithStyle("font-size: 0.85rem; color: var(--neutral-foreground-hint);");

    private static UiControl StatusBadge(LayoutAreaHost host, ApprovalStatus status)
    {
        var (background, foreground) = status switch
        {
            ApprovalStatus.Pending => ("#fff8e1", "#b8860b"),
            ApprovalStatus.Approved => ("#e8f5e9", "#2e7d32"),
            ApprovalStatus.Rejected => ("#ffebee", "#c62828"),
            _ => ("var(--neutral-fill-rest)", "var(--neutral-foreground-rest)")
        };
        return Controls.Badge(LocalizeStatus(host, status)) with
        {
            BackgroundColor = background,
            Color = foreground
        };
    }

    private static string LocalizeStatus(LayoutAreaHost host, ApprovalStatus status) => status switch
    {
        ApprovalStatus.Pending => host.Localize("approval.statusPending"),
        ApprovalStatus.Approved => host.Localize("approval.statusApproved"),
        ApprovalStatus.Rejected => host.Localize("approval.statusRejected"),
        _ => status.ToString()
    };
}
