using MeshWeaver.Mesh;
using MeshWeaver.Todo;
using MeshWeaver.AI.Application;

namespace MeshWeaverApp1.Portal;

public static class SharedMeshConfiguration
{
    public static TBuilder ConfigurePortalMesh<TBuilder>(this TBuilder builder)
    where TBuilder : MeshBuilder
    {
        return (TBuilder)builder.ConfigureMesh(mesh => mesh
                .InstallAssemblies(typeof(AgentsApplicationAttribute).Assembly.Location)
                .InstallAssemblies(typeof(TodoApplicationAttribute).Assembly.Location)
            );
    }

}
