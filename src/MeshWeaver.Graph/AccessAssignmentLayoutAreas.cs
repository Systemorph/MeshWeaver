using System.ComponentModel;
using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Layout areas for AccessAssignment nodes.
/// - Overview: Read-only display of SubjectId, RoleId, and Allow/Deny switch (disabled).
/// - Edit: Same layout with active switch and delete button.
/// </summary>
public static class AccessAssignmentLayoutAreas
{
    public const string OverviewArea = "Overview";
    public const string EditArea = "Edit";

    /// <summary>
    /// Adds the AccessAssignment views to the hub's layout.
    /// </summary>
    public static MessageHubConfiguration AddAccessAssignmentViews(this MessageHubConfiguration configuration)
        => configuration.AddLayout(layout => layout
            .WithDefaultArea(OverviewArea)
            .WithView(OverviewArea, Overview)
            .WithView(EditArea, Edit)
            .WithView(MeshNodeLayoutAreas.DeleteArea, DeleteLayoutArea.Delete));

    /// <summary>
    /// Renders the Overview area (read-only) for an AccessAssignment node.
    /// Shows SubjectId/DisplayName, RoleId, and a disabled Allow/Deny switch.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? [])
            ?? Observable.Return<MeshNode[]>([]);

        return nodeStream.Select(nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            return (UiControl?)BuildAssignmentRow(node, hubPath, editable: false);
        });
    }

    /// <summary>
    /// Renders the Edit area for an AccessAssignment node.
    /// Shows SubjectId/DisplayName, RoleId, an active Allow/Deny switch, and a delete button.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Edit(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? [])
            ?? Observable.Return<MeshNode[]>([]);

        return nodeStream.Select(nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            return (UiControl?)BuildAssignmentRow(node, hubPath, editable: true);
        });
    }

    private static UiControl BuildAssignmentRow(MeshNode? node, string hubPath, bool editable, System.Text.Json.JsonSerializerOptions? options = null)
    {
        var assignment = node?.Content as AccessAssignment
            ?? (node?.Content is System.Text.Json.JsonElement je
                ? (options != null
                    ? System.Text.Json.JsonSerializer.Deserialize<AccessAssignment>(je.GetRawText(), options)
                    : System.Text.Json.JsonSerializer.Deserialize<AccessAssignment>(je.GetRawText()))
                : null);

        var subjectDisplay = assignment?.DisplayName ?? assignment?.SubjectId ?? hubPath;
        var roleDisplay = assignment?.Roles is { Count: > 0 }
            ? string.Join(", ", assignment.Roles.Select(r => r.RoleId))
            : "";
        var isActive = assignment == null || !assignment.Roles.Any(r => r.Denied);

        var row = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("padding: 8px 16px; border-bottom: 1px solid var(--neutral-stroke-rest); align-items: center; gap: 12px;")
            .WithView(Controls.Label(subjectDisplay).WithStyle("flex: 1; min-width: 120px;"))
            .WithView(Controls.Label(roleDisplay).WithStyle("flex: 1; min-width: 120px;"))
            .WithView(Controls.Switch(isActive)
                .WithCheckedMessage("Allow")
                .WithUncheckedMessage("Deny"));

        if (editable)
        {
            row = row.WithView(Controls.Button("")
                .WithIconStart(FluentIcons.Delete())
                .WithAppearance(Appearance.Stealth)
                .WithClickAction(async ctx =>
                {
                    var catalog = ctx.Hub.ServiceProvider.GetService<IMeshCatalog>();
                    if (catalog != null && node != null)
                    {
                        await catalog.DeleteNodeAsync(node.Path);
                    }
                }));
        }

        return row;
    }
}
