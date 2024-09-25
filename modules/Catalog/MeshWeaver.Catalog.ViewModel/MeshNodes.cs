using MeshWeaver.Application;
using MeshWeaver.Catalog.ViewModel;
using MeshWeaver.Mesh.Contract;
using MeshWeaver.Messaging;
[assembly: CatalogApplication]

namespace MeshWeaver.Catalog.ViewModel;

public class CatalogApplicationAttribute : MeshNodeAttribute
{
    public override IMessageHub Create(IServiceProvider serviceProvider, object address)
        => serviceProvider.CreateMessageHub(
            address,
            application =>
                application
                    .AddCatalogViewModels()
        );


    public override MeshNode Node =>
        GetMeshNode(
            new ApplicationAddress("Catalog", "dev"),
            typeof(CatalogApplicationAttribute).Assembly.Location
        );
}
