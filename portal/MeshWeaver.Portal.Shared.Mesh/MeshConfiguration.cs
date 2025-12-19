using MeshWeaver.AI.Application;
using MeshWeaver.Documentation;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Insurance.Domain;
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
        // Get the data directory from Documentation assembly location
        var documentationAssembly = typeof(DocumentationApplicationAttribute).Assembly;
        var assemblyDir = Path.GetDirectoryName(documentationAssembly.Location)!;
        var dataDirectory = Path.Combine(assemblyDir, "Data");

        return (TBuilder)builder
            .AddFileSystemPersistence(dataDirectory)
            .InstallAssemblies(typeof(DocumentationApplicationAttribute).Assembly.Location)
            .InstallAssemblies(typeof(NorthwindApplicationAttribute).Assembly.Location)
            .InstallAssemblies(typeof(AgentsApplicationAttribute).Assembly.Location)
            .InstallAssemblies(typeof(TodoApplicationAttribute).Assembly.Location)
            .InstallAssemblies(typeof(InsuranceApplicationAttribute).Assembly.Location)
            .AddKernel();
    }

}
