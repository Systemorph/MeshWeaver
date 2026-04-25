using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeshWeaver.Hosting.Monolith;

public static class MonolithRegistryExtensions
{
    public static MeshBuilder UseMonolithMesh(this MeshBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.AddMeshCatalog();
            services.TryAddSingleton<IRoutingService, MonolithRoutingService>();
            return services;
        });
        return builder.ConfigureHub(conf =>
            conf
                .AddMeshTypes()
                // Start the per-process persistence coordinator on mesh-hub init.
                // All writes via WriteRequest land here; the hub's single-threaded
                // ActionBlock serializes them. See Doc/Architecture/PersistencePipeline.md.
                .WithInitialization(hub => hub.StartPersistenceCoordinator())
            );
    }
}
