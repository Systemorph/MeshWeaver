using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Connection.Orleans;

public interface IAddressRegistryGrain : IGrainWithStringKey
{
    Task<MeshNode> GetMeshNode();
    Task<StorageInfo> GetStorageInfo();
    Task<StartupInfo> GetStartupInfo();
    Task Unregister();
    Task RegisterNode(StreamInfo streamInfo);
}
