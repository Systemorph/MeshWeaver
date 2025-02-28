using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Connection.Orleans;

public interface IStreamRegistryGrain : IGrainWithStringKey
{
    Task<StreamInfo> Get();
    Task Register(StreamInfo streamInfo);
    Task Unregister();
}
