using MeshWeaver.Application;
using MeshWeaver.Mesh.Contract;
using MeshWeaver.Messaging;

namespace MeshWeaver.MeshBrowser.ViewModel;

public class MeshBrowserAttribute : MeshNodeAttribute
{
    public override IMessageHub Create(IServiceProvider serviceProvider, object address)
        => serviceProvider.CreateMessageHub(address, config => config.AddMeshBrowserViewModels());

    public override MeshNode Node =>
        GetMeshNode(new ApplicationAddress("MeshWeaver", "Catalog"), typeof(MeshBrowserAttribute).Assembly.Location);
}
