using MeshWeaver.Application;
using MeshWeaver.Mesh.Contract;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting.Monolith;

public static class MonolithRegistryExtensions
{
    public static MeshBuilder AddMonolithMesh(this MeshBuilder builder)
    {
        builder.ConfigureServices(services => services
            .AddSingleton<IMeshCatalog, MonolithMeshCatalog>()
            .AddSingleton<IRoutingService, MonolithRoutingService>()
        );
        return builder.ConfigureHub(conf => 
            conf
                .WithTypes(typeof(ApplicationAddress))
                .WithInitialization((hub,ct) => hub.ServiceProvider.GetRequiredService<IMeshCatalog>().InitializeAsync(ct)));
    }
}
