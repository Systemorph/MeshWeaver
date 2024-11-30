using MeshWeaver.Mesh.Contract;
using MeshWeaver.Messaging;

namespace MeshWeaver.Notebooks.Hub;

public static class NotebookExtensions
{
    public static MeshConfiguration AddNotebooks(this MeshConfiguration config)
        => config.WithMeshNodeFactory((addressType, id) => addressType == typeof(NotebookAddress).FullName
            ? MeshExtensions.GetMeshNode(addressType, id, typeof(NotebookExtensions).Assembly.Location)
            : null);

    public static IMessageHub CreateNotebookHub(this IServiceProvider serviceProvider, NotebookAddress address)
    => serviceProvider.CreateMessageHub(address, config => config.WithTypes(typeof(NotebookAddress)));
}
