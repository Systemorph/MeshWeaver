using MeshWeaver.Graph;
using MeshWeaver.Hosting.Persistence.Http;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeshWeaver.InstanceSync;

/// <summary>
/// Wiring for the remote-instance sync feature (mirrors <c>GitHubSyncConfiguration</c>).
/// Three entry points the host composes:
/// <list type="bullet">
///   <item><see cref="AddInstanceSyncServices"/> — DI (config service, coordinator hosted
///     service, admin-page provider) on the app service collection;</item>
///   <item><see cref="AddInstanceSyncTypes{TBuilder}"/> — registers the
///     <see cref="InstanceSyncConfig"/> content type on the mesh hub + every per-node hub so
///     the <c>{space}/_Sync/{sourceId}</c> nodes (de)serialize, plus the "Synchronizations"
///     node-menu item and the <see cref="InstanceSyncLayoutArea"/> management view it opens.</item>
/// </list>
/// </summary>
public static class InstanceSyncConfiguration
{
    /// <summary>Registers the instance-sync services as mesh-scoped singletons plus the
    /// coordinator hosted service that runs the sync workers.</summary>
    public static IServiceCollection AddInstanceSyncServices(this IServiceCollection services)
    {
        services.AddSingleton(new InstanceSyncOptions());
        // The MCP-over-HTTP client toward the remote instance (IoPool-bounded). TryAdd so a
        // host (or test) that registered its own factory wins.
        services.TryAddSingleton<IRemoteMeshClientFactory, McpRemoteMeshClientFactory>();
        services.AddSingleton<InstanceSyncService>();
        services.AddSingleton<InstanceSyncCoordinator>();
        services.AddHostedService(sp => sp.GetRequiredService<InstanceSyncCoordinator>());
        // Surfaces the per-space remote-instance sync sources on the partition administration
        // page (PartitionSyncAdminLayoutArea resolves all IPartitionSyncSourceProvider from DI).
        services.AddSingleton<IPartitionSyncSourceProvider>(sp => new InstanceSyncPartitionSyncSourceProvider(
            sp.GetRequiredService<InstanceSyncService>()));
        return services;
    }

    /// <summary>
    /// Registers the instance-sync content type on the mesh hub AND every per-node hub so the
    /// <c>{spaceId}/_Sync/{sourceId}</c> config nodes (de)serialize with a resolvable
    /// <c>$type</c> (avoids the content-discriminator reject + JsonElement degradation).
    /// </summary>
    public static TBuilder AddInstanceSyncTypes<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(
            new MeshNode(InstanceSyncService.ConfigNodeType)
            {
                Name = "Instance Sync Config",
                IsSatelliteType = false,
                ExcludeFromContext = new HashSet<string> { "search", "create" },
                HubConfiguration = config => config
                    .AddMeshDataSource(source => source.WithContentType<InstanceSyncConfig>()),
            });

        builder.ConfigureHub(c => c
            .WithType<InstanceSyncConfig>(nameof(InstanceSyncConfig))
            .WithType<PendingChange>(nameof(PendingChange)));
        builder.ConfigureDefaultNodeHub(c => c
            .WithType<InstanceSyncConfig>(nameof(InstanceSyncConfig))
            .WithType<PendingChange>(nameof(PendingChange))
            // The "Synchronizations" NODE-menu item + the management area it opens. The provider is
            // per-node-hub scoped (TryAddEnumerable) so the menu render running on the node hub
            // resolves it; it self-gates to Spaces the viewer may Update.
            .WithServices(s =>
            {
                s.TryAddEnumerable(ServiceDescriptor.Scoped<INodeMenuProvider, InstanceSyncMenuProvider>());
                return s;
            })
            .AddLayout(layout => layout
                .WithView(InstanceSyncLayoutArea.AreaName, InstanceSyncLayoutArea.Render)));
        return builder;
    }
}
