using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Connection.Orleans;

public interface IAddressRegistryGrain : IGrainWithStringKey
{
    Task<StreamInfo> GetStreamInfo();
    Task<NodeStorageInfo> GetStorageInfo();
    Task Register(StreamInfo streamInfo);
    Task Unregister();
}
