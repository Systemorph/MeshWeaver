using System.Reactive.Linq;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph;

/// <summary>
/// Layout areas for GroupMembership nodes.
/// Custom Thumbnail shows compact row with avatar + name + group chips.
/// Custom Overview shows property form without children section.
/// </summary>
public static class GroupMembershipLayoutAreas
{
    /// <summary>
    /// Adds the GroupMembership views to the hub's layout.
    /// Registers custom Thumbnail and Overview, plus Delete.
    /// </summary>
    public static MessageHubConfiguration AddGroupMembershipViews(this MessageHubConfiguration configuration)
        => configuration.AddLayout(layout => layout
            .WithView(MeshNodeLayoutAreas.ThumbnailArea, Thumbnail)
            .WithView(MeshNodeLayoutAreas.OverviewArea, Overview)
            .WithView(MeshNodeLayoutAreas.DeleteArea, DeleteLayoutArea.Delete));

    /// <summary>
    /// Custom thumbnail for GroupMembership nodes.
    /// Shows a compact row: [Avatar] [Name] [Group chips] with navigation.
    /// </summary>
    public static IObservable<UiControl?> Thumbnail(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        return host.StreamView<MeshNode>(
            (nodes, _) =>
            {
                var node = nodes.FirstOrDefault(n => n.Path == hubPath);
                var membership = GroupsLayoutArea.DeserializeMembership(node!);
                if (membership == null)
                    return MeshNodeThumbnailControl.FromNode(node, hubPath); // fallback

                return GroupMembershipControlBuilder.Build(
                    membership,
                    node: node,
                    navigateTo: $"/{hubPath}");
            },
            hubPath);
    }

    /// <summary>
    /// Custom overview for GroupMembership nodes.
    /// Shows the property form (Member + Groups) but suppresses children section.
    /// </summary>
    public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var ownNode = host.Workspace.GetMeshNodeStream();
        var permsStream = PermissionHelper.GetEffectivePermissions(host.Hub, hubPath);

        return ownNode.CombineLatest(permsStream, (node, perms) =>
            {
                if (!perms.HasFlag(Permission.Read))
                    return (UiControl?)Controls.Html("<p>Access denied.</p>");
                var canEdit = perms.HasFlag(Permission.Update);
                var stack = Controls.Stack.WithWidth("100%").WithStyle(MeshNodeLayoutAreas.GetContainerStyle(host));
                stack = stack.WithView(MeshNodeLayoutAreas.BuildHeader(host, node, canEdit));
                stack = stack.WithView(OverviewLayoutArea.BuildPropertyOverview(host, node, canEdit));
                return (UiControl?)stack;
            });
    }
}
