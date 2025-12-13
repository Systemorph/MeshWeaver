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
            // Register default in-memory persistence if not already registered
            services.TryAddSingleton<IPersistenceService>(new InMemoryPersistenceService());

            return services
                .AddSingleton<IMeshCatalog, InMemoryMeshCatalog>()
                .AddSingleton<IRoutingService, MonolithRoutingService>();
        });
        return builder.ConfigureHub(conf =>
            conf
                .AddMeshTypes()
            );
    }
}
