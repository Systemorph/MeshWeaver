using MeshWeaver.Mesh.Contract;

namespace MeshWeaver.Hosting.Orleans.Client;

public interface IAddressRegistryGrain : IGrainWithStringKey
{
    Task<StreamInfo> Register(object address);
    Task<NodeStorageInfo> GetStorageInfo();
    Task Register(StreamInfo streamInfo);
    Task Unregister();
}
