using MeshWeaver.Mesh;

namespace MeshWeaver.Connection.Orleans;

public interface ITestGrain : IGrainWithStringKey
{
    Task Something();
}
public interface IMeshNodeGrain : IGrainWithStringKey
{
    Task<MeshNode> Get();
    Task Update(MeshNode entry);
    Task Delete();
}
