using MeshWeaver.Mesh.Contract;

namespace MeshWeaver.Notebooks.Hub;

public static class NotebooksExtensions
{
    public static MeshConfiguration AddNotebooks(this MeshConfiguration config)
        => config.WithMeshNodeFactory((addressType, id) => addressType == typeof(NotebookAddress).FullName
            ? MeshExtensions.GetMeshNode(addressType, id, typeof(NotebooksExtensions).Assembly.Location)
            : null);
}
