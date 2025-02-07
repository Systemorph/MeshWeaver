using MeshWeaver.Articles;
using MeshWeaver.Documentation;
using MeshWeaver.Kernel.Hub;
using MeshWeaver.Mesh;
using MeshWeaver.Northwind.ViewModel;

namespace MeshWeaver.Portal.Shared.Services;

public static  class SharedMeshConfiguration
{
    public static MeshBuilder ConfigurePortalMesh(this MeshBuilder builder)
    {
        return builder.ConfigureMesh(mesh => mesh
                .InstallAssemblies(typeof(DocumentationViewModels).Assembly.Location)
                .InstallAssemblies(typeof(NorthwindViewModels).Assembly.Location)
            )
            .AddKernel()
            .AddArticles(articles
                => articles.FromAppSettings()
            );
    }
}
