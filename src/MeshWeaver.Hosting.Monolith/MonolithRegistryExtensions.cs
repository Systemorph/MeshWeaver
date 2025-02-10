using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting.Monolith;

public static class MonolithRegistryExtensions
{
    public static MeshBuilder UseMonolithMesh(this MeshBuilder builder)
    {
        builder.ConfigureServices(services => services
            .AddSingleton<IMeshCatalog, MonolithMeshCatalog>()
            .AddSingleton<IRoutingService, MonolithRoutingService>()
        );
        return builder.ConfigureHub(conf =>
            conf
                .AddMeshTypes()
            );
    }


}
