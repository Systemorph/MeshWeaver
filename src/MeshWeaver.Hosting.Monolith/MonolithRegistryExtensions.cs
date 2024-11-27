using MeshWeaver.Mesh.Contract;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting.Monolith;

public static class MonolithRegistryExtensions
{
    public static MeshBuilder AddMonolithMesh(this MeshBuilder builder)
    {
        builder.Services.AddSingleton<IMeshCatalog, MonolithMeshCatalog>();
        builder.Services.AddSingleton<IRoutingService, MonolithRoutingService>();
        return builder.ConfigureHub(conf => 
            conf.WithInitialization((hub,ct) => hub.ServiceProvider.GetRequiredService<IMeshCatalog>().InitializeAsync(ct)));
    }
}
