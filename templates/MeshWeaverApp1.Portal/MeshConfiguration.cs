using MeshWeaver.AI.Application;
using MeshWeaver.Kernel.Hub;
using MeshWeaver.Mesh;
using MeshWeaverApp1.Todo;

namespace MeshWeaverApp1.Portal;

public static class SharedMeshConfiguration
{
    public static TBuilder ConfigurePortalMesh<TBuilder>(this TBuilder builder)
    where TBuilder : MeshBuilder
    {
        return (TBuilder)builder.ConfigureMesh(mesh => mesh
                .InstallAssemblies(typeof(MeshWeaver.AI.Application.AgentsApplicationAttribute).Assembly.Location)
                .InstallAssemblies(typeof(MeshWeaverApp1.Todo.TodoApplicationAttribute).Assembly.Location)
            )
            .AddKernel();
    }

}
