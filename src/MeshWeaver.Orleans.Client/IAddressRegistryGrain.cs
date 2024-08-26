using MeshWeaver.Mesh.Contract;
using Orleans;

namespace MeshWeaver.Orleans.Client;

public interface IAddressRegistryGrain : IGrainWithStringKey
{
    Task<StreamInfo> Register(object address);
    Task<NodeStorageInfo> GetStorageInfo();
    Task Register(StreamInfo streamInfo);
    Task Unregister();
}
