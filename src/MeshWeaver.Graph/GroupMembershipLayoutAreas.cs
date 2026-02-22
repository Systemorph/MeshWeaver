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
/// Layout areas for GroupMembership nodes.
/// - Overview: Read-only display of the member reference.
/// - Edit: Same display plus a Delete button.
/// </summary>
public static class GroupMembershipLayoutAreas
{
    public const string OverviewArea = "Overview";
    public const string EditArea = "Edit";

    /// <summary>
    /// Adds the GroupMembership views to the hub's layout.
    /// </summary>
    public static MessageHubConfiguration AddGroupMembershipViews(this MessageHubConfiguration configuration)
        => configuration.AddLayout(layout => layout
            .WithDefaultArea(OverviewArea)
            .WithView(OverviewArea, Overview)
            .WithView(EditArea, Edit)
            .WithView(MeshNodeLayoutAreas.DeleteArea, DeleteLayoutArea.Delete));

    /// <summary>
    /// Renders the Overview area (read-only) for a GroupMembership node.
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
            return (UiControl?)BuildMembershipRow(node, hubPath, editable: false);
        });
    }

    /// <summary>
    /// Renders the Edit area for a GroupMembership node.
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
            return (UiControl?)BuildMembershipRow(node, hubPath, editable: true);
        });
    }

    private static UiControl BuildMembershipRow(MeshNode? node, string hubPath, bool editable)
    {
        var membership = node?.Content as GroupMembership
            ?? (node?.Content is System.Text.Json.JsonElement je
                ? System.Text.Json.JsonSerializer.Deserialize<GroupMembership>(je.GetRawText())
                : null);

        var memberDisplay = membership?.Id ?? node?.Name ?? hubPath;

        var row = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("padding: 8px 16px; border-bottom: 1px solid var(--neutral-stroke-rest); align-items: center; gap: 12px;")
            .WithView(Controls.Icon(FluentIcons.Person()).WithStyle("font-size: 20px;"))
            .WithView(Controls.Label(memberDisplay).WithStyle("flex: 1;"));

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
