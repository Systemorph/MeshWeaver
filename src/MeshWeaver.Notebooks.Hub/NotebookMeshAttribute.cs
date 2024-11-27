using MeshWeaver.Mesh.Contract;
using MeshWeaver.Messaging;

namespace MeshWeaver.Notebooks.Hub;

public class NotebookMeshAttribute : MeshNodeAttribute
{
    public override IMessageHub Create(IServiceProvider serviceProvider, MeshNode node)
    {
        if (node.AddressType != typeof(NotebookAddress).FullName)
            return null;
        return serviceProvider
            .CreateMessageHub(new NotebookAddress(node.Id), 
                config => config);
    }
}
