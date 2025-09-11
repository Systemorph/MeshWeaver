using MeshWeaver.AI.Application;
using MeshWeaver.Documentation;
using MeshWeaver.Kernel.Hub;
using MeshWeaver.Mesh;
using MeshWeaver.Northwind.Application;
using MeshWeaver.Todo;

namespace MeshWeaver.Portal.Shared.Mesh;

public static  class SharedMeshConfiguration
{
    public static TBuilder ConfigurePortalMesh<TBuilder>(this TBuilder builder)
    where TBuilder:MeshBuilder
    {
        return (TBuilder)builder
            .InstallAssemblies(typeof(DocumentationApplicationAttribute).Assembly.Location)
            .InstallAssemblies(typeof(NorthwindApplicationAttribute).Assembly.Location)
            .InstallAssemblies(typeof(AgentsApplicationAttribute).Assembly.Location)
            .InstallAssemblies(typeof(TodoApplicationAttribute).Assembly.Location)
            .AddKernel();
    }

}
