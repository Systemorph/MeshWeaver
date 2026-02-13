using MeshWeaver.Kernel.Hub;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Hosting.Cosmos.Test;

public static class CosmosTestMeshExtensions
{
    public static MeshBuilder ConfigurePortalMesh(this MeshBuilder builder)
    {
        var assemblyLocation = typeof(CosmosTestMeshExtensions).Assembly.Location;
        return builder
            .InstallAssemblies(assemblyLocation)
            .AddMeshNodes(MeshNode.FromPath($"{AddressExtensions.AppType}/HubFactory") with
            {
                Name = "HubFactory",
                AssemblyLocation = assemblyLocation,
                HubConfiguration = x => x
            })
            .AddMeshNodes(MeshNode.FromPath($"{AddressExtensions.AppType}/Kernel") with
            {
                Name = "Kernel",
                AssemblyLocation = assemblyLocation,
                HubConfiguration = x => x
            })
            .AddKernel();
    }

    public static MessageHubConfiguration ConfigureCosmosTestApplication(this MessageHubConfiguration configuration)
        => configuration;
}
