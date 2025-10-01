using MeshWeaver.Kernel.Hub;
using MeshWeaver.Mesh;

namespace MeshWeaverApp1.Portal;

public static class SharedMeshConfiguration
{
    public static TBuilder ConfigurePortalMesh<TBuilder>(this TBuilder builder)
    where TBuilder : MeshBuilder => (TBuilder)builder
                .InstallAssemblies(typeof(MeshWeaver.AI.Application.AgentsApplicationAttribute).Assembly.Location)
                .InstallAssemblies(typeof(MeshWeaverApp1.Todo.TodoApplicationAttribute).Assembly.Location)
            .AddKernel();

}
