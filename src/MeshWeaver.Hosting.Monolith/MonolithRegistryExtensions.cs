using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeshWeaver.Hosting.Monolith;

/// <summary>
/// Extension methods that configure a <c>MeshBuilder</c> for running the mesh in a single-process
/// (monolith) hosting model, wiring up the in-process catalog, routing, and query services.
/// </summary>
public static class MonolithRegistryExtensions
{
    /// <summary>
    /// Configures the supplied builder for monolith hosting: registers the mesh catalog and an
    /// in-process <c>IRoutingService</c>, adds the mesh message types to the hub, and enables the
    /// core mesh query handling on the mesh hub.
    /// </summary>
    /// <param name="builder">The mesh builder to configure.</param>
    /// <returns>The same <paramref name="builder"/> instance, enabling fluent chaining.</returns>
    public static MeshBuilder UseMonolithMesh(this MeshBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.AddMeshCatalog();
            services.TryAddSingleton<IRoutingService, MonolithRoutingService>();
            return services;
        });
        builder.ConfigureHub(conf =>
            conf
                .AddMeshTypes()
            );
        return builder.RegisterMeshQueryCoreOnMeshHub();
    }
}
