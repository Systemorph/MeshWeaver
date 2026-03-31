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
            );
    }
}
