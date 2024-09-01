using MeshWeaver.Mesh.Contract;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting.Monolith;

public static class MonolithRegistryExtensions
{
    public static TBuilder AddMonolithMesh<TBuilder>(this TBuilder builder)
        where TBuilder:MeshWeaverApplicationBuilder<TBuilder>
    {
        builder.Host.Services.AddSingleton<IMeshCatalog, MonolithMeshCatalog>();
        builder.Host.Services.AddSingleton<IRoutingService, MonolithRoutingService>();
        return builder;
    }
}
