using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Views used by ApprovalExtensions:
/// - RequestApproval: Form to request approval on the current node.
/// - InlineApprovals: Inline section showing existing approvals for the node.
/// </summary>
public static class ApprovalsView
{
    /// <summary>
    /// Request Approval form — user select, purpose text, due date.
    /// On submit: creates Approval node under document namespace, creates notification for approver.
    /// </summary>
    public static IObservable<UiControl?> RequestApproval(LayoutAreaHost host, RenderingContext _)
    {
        var nodePath = host.Hub.Address.ToString();
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var currentUser = accessService?.Context?.ObjectId ?? "";

        var formDataId = $"approvalForm_{nodePath.Replace("/", "_")}";
        host.UpdateData(formDataId, new Dictionary<string, object?>
        {
            ["approver"] = "",
            ["purpose"] = "",
            ["dueDate"] = ""
        });

        return Observable.Return((UiControl?)BuildRequestForm(host, nodePath, currentUser, formDataId));
    }

    private static UiControl BuildRequestForm(LayoutAreaHost host, string nodePath, string currentUser, string formDataId)
    {
        var container = Controls.Stack.WithWidth("100%").WithStyle(MeshNodeLayoutAreas.GetContainerStyle(host));

        container = container.WithView(Controls.Html("<h2 style=\"margin: 0 0 16px 0;\">Request Approval</h2>"));

        var form = Controls.Stack.WithWidth("100%").WithStyle("gap: 12px;");

        // Approver input
        form = form.WithView(Controls.Html("<label style=\"font-weight: 600;\">Approver (User ID)</label>"));
        form = form.WithView(new MarkdownEditorControl()
            .WithDocumentId($"{nodePath}_approver")
            .WithHeight("40px")
            .WithPlaceholder("Enter user ObjectId...") with
        {
            Value = new JsonPointerReference("approver"),
            DataContext = LayoutAreaReference.GetDataPointer(formDataId)
        });

        // Purpose input
        form = form.WithView(Controls.Html("<label style=\"font-weight: 600;\">Purpose</label>"));
        form = form.WithView(new MarkdownEditorControl()
            .WithDocumentId($"{nodePath}_purpose")
            .WithHeight("60px")
            .WithPlaceholder("Why is this approval needed?") with
        {
            Value = new JsonPointerReference("purpose"),
            DataContext = LayoutAreaReference.GetDataPointer(formDataId)
        });

        // Due date input
        form = form.WithView(Controls.Html("<label style=\"font-weight: 600;\">Due Date (optional, YYYY-MM-DD)</label>"));
        form = form.WithView(new MarkdownEditorControl()
            .WithDocumentId($"{nodePath}_dueDate")
            .WithHeight("40px")
            .WithPlaceholder("2026-03-15") with
        {
            Value = new JsonPointerReference("dueDate"),
            DataContext = LayoutAreaReference.GetDataPointer(formDataId)
        });

        container = container.WithView(form);

        // Submit / Cancel buttons
        var buttons = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap(8)
            .WithStyle("margin-top: 16px;");

        buttons = buttons.WithView(Controls.Button("Submit")
            .WithAppearance(Appearance.Accent)
            .WithClickAction(async ctx =>
            {
                await SubmitApprovalRequest(ctx.Host, nodePath, currentUser, formDataId);
            }));

        buttons = buttons.WithView(Controls.Button("Cancel")
            .WithAppearance(Appearance.Neutral)
            .WithClickAction(_ =>
            {
                host.Hub.ServiceProvider.GetService<INavigationService>()
                    ?.NavigateTo($"/{nodePath}");
                return Task.CompletedTask;
            }));

        container = container.WithView(buttons);

        return container;
    }

    private static async Task SubmitApprovalRequest(LayoutAreaHost host, string nodePath, string currentUser, string formDataId)
    {
        var approver = "";
        var purpose = "";
        var dueDateStr = "";

        host.Stream.GetDataStream<Dictionary<string, object?>>(formDataId)
            .Take(1)
            .Subscribe(data =>
            {
                approver = data?.GetValueOrDefault("approver")?.ToString() ?? "";
                purpose = data?.GetValueOrDefault("purpose")?.ToString() ?? "";
                dueDateStr = data?.GetValueOrDefault("dueDate")?.ToString() ?? "";
            });

        if (string.IsNullOrWhiteSpace(approver))
            return;

        DateTimeOffset? dueDate = null;
        if (DateTimeOffset.TryParse(dueDateStr, out var parsed))
            dueDate = parsed;

        var approvalId = Guid.NewGuid().AsString();
        var approvalPath = $"{nodePath}/{approvalId}";

        var approval = new Approval
        {
            Id = approvalId,
            PrimaryNodePath = nodePath,
            Requester = currentUser,
            Approver = approver,
            Purpose = purpose,
            DueDate = dueDate,
            CreatedAt = DateTimeOffset.UtcNow,
            Status = ApprovalStatus.Pending
        };

        var meshCatalog = host.Hub.ServiceProvider.GetRequiredService<IMeshCatalog>();

        var approvalNode = new MeshNode(approvalId, nodePath)
        {
            Name = $"Approval: {purpose}",
            NodeType = ApprovalNodeType.NodeType,
            State = MeshNodeState.Active,
            Content = approval
        };

        await meshCatalog.CreateNodeAsync(approvalNode, currentUser);

        // Create notification for the approver
        await NotificationService.CreateNotificationAsync(
            meshCatalog,
            approver,
            "Approval Requested",
            $"{currentUser} requested your approval for \"{purpose}\".",
            NotificationType.ApprovalRequired,
            nodePath,
            currentUser);

        // Navigate back to the document
        host.Hub.ServiceProvider.GetService<INavigationService>()?.NavigateTo($"/{nodePath}");
    }

    /// <summary>
    /// Inline approvals section for embedding in Markdown overview.
    /// Queries for child Approval nodes. If none exist, returns empty stack (no visible section).
    /// </summary>
    public static IObservable<UiControl?> InlineApprovals(LayoutAreaHost host, RenderingContext _)
    {
        var nodePath = host.Hub.Address.ToString();
        var meshQuery = host.Hub.ServiceProvider.GetService<IMeshQuery>();

        if (meshQuery == null)
            return Observable.Return<UiControl?>(null);

        var approvalsDataId = $"inlineApprovals_{nodePath.Replace("/", "_")}";
        host.UpdateData(approvalsDataId, Array.Empty<LayoutAreaControl>());

        meshQuery.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                $"path:{nodePath} nodeType:{ApprovalNodeType.NodeType} scope:children"))
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
                var controls = list
                    .Where(n => n.Content is Approval)
                    .OrderByDescending(n => ((Approval)n.Content!).CreatedAt)
                    .Select(n => Controls.LayoutArea(n.Path, ApprovalLayoutAreas.OverviewArea))
                    .ToArray();
                host.UpdateData(approvalsDataId, controls);
            });

        return host.Stream.GetDataStream<LayoutAreaControl[]>(approvalsDataId)
            .Select(approvalControls =>
            {
                if (approvalControls == null || approvalControls.Length == 0)
                    return (UiControl?)Controls.Stack; // Empty — no section shown

                var section = Controls.Stack.WithWidth("100%")
                    .WithStyle("margin-top: 32px; border-top: 1px solid var(--neutral-stroke-rest); padding-top: 16px;");
                section = section.WithView(Controls.Html("<h3 style=\"margin: 0 0 12px 0;\">Approvals</h3>"));

                foreach (var ctrl in approvalControls)
                    section = section.WithView(ctrl);

                return (UiControl?)section;
            });
    }
}
