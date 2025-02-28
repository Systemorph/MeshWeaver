using MeshWeaver.Articles;
using MeshWeaver.Kernel.Hub;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Hosting.Orleans.Test;

public static class OrleansTestMeshExtensions
{
    public static MeshBuilder ConfigurePortalMesh(this MeshBuilder builder)
    {
        return builder.ConfigureMesh(mesh => mesh
                .InstallAssemblies(typeof(OrleansTestMeshExtensions).Assembly.Location)
            )
            .AddKernel();

    }

    public static MessageHubConfiguration ConfigureOrleansTestApplication(this MessageHubConfiguration configuration)
        => configuration;
}
