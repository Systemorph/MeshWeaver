using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.InstanceSync;

/// <summary>
/// DI-registered <see cref="INodeMenuProvider"/> that contributes the Space's remote-instance
/// SYNC actions to their OWN "Sync" dropdown — shown only when the containing Space has a
/// configured <c>_Sync</c> registration and the viewer may Update it. One submenu per registration
/// (its name) with Sync now / Pause·Resume / Remove; each navigates to
/// <see cref="InstanceSyncActionArea"/>. Setup (URL / token / direction) stays in the settings tab.
/// </summary>
public sealed class InstanceSyncMenuProvider : INodeMenuProvider
{
    /// <inheritdoc />
    public string Context => NodeMenuItemsExtensions.SyncMenuContext;

    /// <inheritdoc />
    public IObservable<IReadOnlyCollection<NodeMenuItemDefinition>> GetItems(
        LayoutAreaHost host, RenderingContext ctx)
    {
        var hubPath = host.Hub.Address.ToString();
        var spacePath = InstanceSyncService.SpaceOf(hubPath);
        if (string.IsNullOrEmpty(spacePath))
            return Observable.Return<IReadOnlyCollection<NodeMenuItemDefinition>>([]);

        var sync = host.Hub.ServiceProvider.GetService<InstanceSyncService>();
        if (sync is null)
            return Observable.Return<IReadOnlyCollection<NodeMenuItemDefinition>>([]);

        // Live: re-projects when a registration is added/removed/edited OR the viewer's permission
        // on the Space enriches (the access-race fix). StartWith so CombineLatest fires immediately.
        return sync.WatchConfigNodes(spacePath).StartWith([])
            .CombineLatest(
                host.Hub.GetEffectivePermissions(spacePath),
                (configs, perms) =>
                {
                    if (!perms.HasFlag(Permission.Update))
                        return (IReadOnlyCollection<NodeMenuItemDefinition>)[];

                    var items = new List<NodeMenuItemDefinition>();
                    var order = 0;
                    foreach (var node in configs)
                    {
                        var cfg = sync.Extract(node);
                        if (cfg is not { IsConfigured: true })
                            continue;   // an unconfigured party has nothing to act on yet
                        var id = node.Id;
                        var active = cfg.Active;
                        items.Add(new NodeMenuItemDefinition(
                            Label: node.Name ?? id,
                            Area: InstanceSyncActionArea.AreaName,
                            Icon: "ArrowSync",
                            Order: order++,
                            Tooltip: InstanceSyncPartitionSyncSourceProvider.Describe(cfg),
                            Children:
                            [
                                new NodeMenuItemDefinition("Sync now", InstanceSyncActionArea.AreaName,
                                    Icon: "ArrowSync", Order: 0,
                                    Href: InstanceSyncActionArea.Href(hubPath, id, InstanceSyncActionArea.SyncNow)),
                                new NodeMenuItemDefinition(active ? "Pause" : "Resume", InstanceSyncActionArea.AreaName,
                                    Icon: active ? "Pause" : "Play", Order: 1,
                                    Href: InstanceSyncActionArea.Href(hubPath, id, InstanceSyncActionArea.Pause)),
                                new NodeMenuItemDefinition("Remove", InstanceSyncActionArea.AreaName,
                                    Icon: "Delete", Order: 2,
                                    Href: InstanceSyncActionArea.Href(hubPath, id, InstanceSyncActionArea.Remove)),
                            ]));
                    }
                    return items;
                });
    }
}
