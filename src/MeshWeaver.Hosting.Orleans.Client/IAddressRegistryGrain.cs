using MeshWeaver.Mesh;

namespace MeshWeaver.Hosting.Orleans.Client;

public interface IAddressRegistryGrain : IGrainWithStringKey
{
    Task<StreamInfo> GetStreamInfo(string addressType, string id);
    Task<NodeStorageInfo> GetStorageInfo();
    Task Register(StreamInfo streamInfo);
    Task Unregister();
}
