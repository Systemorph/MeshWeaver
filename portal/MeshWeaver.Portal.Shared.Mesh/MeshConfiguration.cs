using MeshWeaver.AI.Application;
using MeshWeaver.Documentation;
using MeshWeaver.Kernel.Hub;
using MeshWeaver.Mesh;
using MeshWeaver.Northwind.Application;

namespace MeshWeaver.Portal.Shared.Mesh;

public static  class SharedMeshConfiguration
{
    public static TBuilder ConfigurePortalMesh<TBuilder>(this TBuilder builder)
    where TBuilder:MeshBuilder
    {
        return (TBuilder)builder.ConfigureMesh(mesh => mesh
                .InstallAssemblies(typeof(DocumentationViewModels).Assembly.Location)
                .InstallAssemblies(typeof(NorthwindViewModels).Assembly.Location)
                .InstallAssemblies(typeof(AgentsApplicationNodeAttribute).Assembly.Location)
            )
            .AddKernel();
    }

}
