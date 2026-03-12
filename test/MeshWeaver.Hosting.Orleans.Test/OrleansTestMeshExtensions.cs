using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Hosting.Orleans.Test;

public static class OrleansTestMeshExtensions
{
    public static MeshBuilder ConfigurePortalMesh(this MeshBuilder builder)
    {
        var assemblyLocation = typeof(OrleansTestMeshExtensions).Assembly.Location;
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

    public static MessageHubConfiguration ConfigureOrleansTestApplication(this MessageHubConfiguration configuration)
        => configuration;
}
