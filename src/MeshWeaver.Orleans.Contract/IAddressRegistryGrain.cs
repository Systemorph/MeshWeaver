using Orleans;

namespace MeshWeaver.Orleans.Contract;

public interface IAddressRegistryGrain : IGrainWithStringKey
{
    Task<StreamInfo> Register(object address);
    Task<NodeStorageInfo> GetStorageInfo();
    Task Register(StreamInfo streamInfo);
    Task Unregister();
}
public record StreamInfo(string Id, string StreamProvider, string Namespace, object Address);
public record NodeStorageInfo(string NodeId, string BaseDirectory, string AssemblyLocation, object Address);
