using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph;

/// <summary>
/// DI-registered <see cref="INodeMenuProvider"/> that adds "Invite people" to the NODE menu on a
/// Space — opening <see cref="SpaceInviteLayoutArea"/>. Shown on a Space (its root or a descendant)
/// for a viewer who may Update it; the access-race fix re-emits when the permission enriches.
/// </summary>
public sealed class SpaceInviteMenuProvider : INodeMenuProvider
{
    /// <summary>The NodeType value identifying a Space.</summary>
    private const string SpaceNodeType = "Space";

    /// <inheritdoc />
    public string Context => NodeMenuItemsExtensions.NodeMenuContext;

    /// <inheritdoc />
    public IObservable<IReadOnlyCollection<NodeMenuItemDefinition>> GetItems(
        LayoutAreaHost host, RenderingContext ctx)
    {
        var hubPath = host.Hub.Address.ToString();
        var spaceRoot = hubPath.Split('/', 2)[0];
        IReadOnlyCollection<NodeMenuItemDefinition> none = [];
        if (string.IsNullOrEmpty(spaceRoot))
            return Observable.Return(none);

        var item = new NodeMenuItemDefinition(
            Label: "Invite people",
            Area: SpaceInviteLayoutArea.AreaName,
            Icon: "✉️",
            Order: 8,   // a Space-management action, at the top of the node menu
            Href: MeshNodeLayoutAreas.BuildUrl(hubPath, SpaceInviteLayoutArea.AreaName),
            Tooltip: "Invite someone to this Space by email");

        return host.Workspace.GetMeshNodeStream(spaceRoot)
            .Select(node => string.Equals(node?.NodeType, SpaceNodeType, StringComparison.Ordinal))
            .CombineLatest(
                host.Hub.GetEffectivePermissions(spaceRoot),
                (isSpace, perms) => isSpace && perms.HasFlag(Permission.Update))
            .DistinctUntilChanged()
            .Select(show => show ? (IReadOnlyCollection<NodeMenuItemDefinition>)[item] : none)
            .Catch<IReadOnlyCollection<NodeMenuItemDefinition>, Exception>(_ => Observable.Return(none))
            .StartWith(none);
    }
}
