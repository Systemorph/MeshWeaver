using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Connection.Orleans;

public interface IAddressRegistryGrain : IGrainWithStringKey
{
    Task<StreamInfo> GetStreamInfo();
    Task<StorageInfo> GetStorageInfo();
    Task<StartupInfo> GetStartupInfo();
    Task Unregister();
    Task RegisterStream(StreamInfo streamInfo);
}
