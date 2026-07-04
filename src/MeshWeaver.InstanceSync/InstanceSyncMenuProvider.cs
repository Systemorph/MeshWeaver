using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.InstanceSync;

/// <summary>
/// DI-registered <see cref="INodeMenuProvider"/> that contributes the "Synchronizations" entry to
/// the NODE menu (instance sync is a per-node/Space operation, sitting in the content-and-history
/// section next to Files / Versions). Selecting it opens <see cref="InstanceSyncLayoutArea"/> — the
/// full sync management view (add party, connection setup, Sync now / Pause·Resume / Remove). Shown
/// on any node within a Space for a viewer who may Update it; there is no settings tab.
/// </summary>
public sealed class InstanceSyncMenuProvider : INodeMenuProvider
{
    /// <inheritdoc />
    public string Context => NodeMenuItemsExtensions.NodeMenuContext;

    /// <inheritdoc />
    public IObservable<IReadOnlyCollection<NodeMenuItemDefinition>> GetItems(
        LayoutAreaHost host, RenderingContext ctx)
    {
        var hubPath = host.Hub.Address.ToString();
        var spacePath = InstanceSyncService.SpaceOf(hubPath);
        IReadOnlyCollection<NodeMenuItemDefinition> none = [];
        if (string.IsNullOrEmpty(spacePath))
            return Observable.Return(none);

        var item = new NodeMenuItemDefinition(
            Label: "Synchronizations",
            Area: InstanceSyncLayoutArea.AreaName,
            Icon: "🔄",
            Order: 36,   // content/history/sync section (Files=30, Versions=32, StopSync=34)
            Href: MeshNodeLayoutAreas.BuildUrl(hubPath, InstanceSyncLayoutArea.AreaName),
            Tooltip: "Sync this Space with other MeshWeaver instances");

        // Live: show on a Space (its root or any descendant) for a viewer who may Update it — the
        // access-race fix re-emits when the permission enriches. StartWith so CombineLatest fires.
        return host.Workspace.GetMeshNodeStream(spacePath)
            .Select(node => string.Equals(node?.NodeType, InstanceSyncService.SpaceNodeType, StringComparison.Ordinal))
            .CombineLatest(
                host.Hub.GetEffectivePermissions(spacePath),
                (isSpace, perms) => isSpace && perms.HasFlag(Permission.Update))
            .DistinctUntilChanged()
            .Select(show => show ? (IReadOnlyCollection<NodeMenuItemDefinition>)[item] : none)
            .Catch<IReadOnlyCollection<NodeMenuItemDefinition>, Exception>(_ => Observable.Return(none))
            .StartWith(none);
    }
}
