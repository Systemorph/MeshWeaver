using MeshWeaver.Mesh.Contract;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting.Monolith;

public static class MonolithRegistryExtensions
{
    public static MeshWeaverHostBuilder AddMonolithMesh(this MeshWeaverHostBuilder builder)
    {
        builder.Host.Services.AddSingleton<IMeshCatalog, MonolithMeshCatalog>();
        builder.Host.Services.AddSingleton<IRoutingService, MonolithRoutingService>();
        return builder.ConfigureHub(conf => conf.WithInitialization((hub,ct) => hub.ServiceProvider.GetRequiredService<IMeshCatalog>().InitializeAsync(ct)));
    }
}
