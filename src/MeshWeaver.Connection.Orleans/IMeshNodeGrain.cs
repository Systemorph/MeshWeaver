using MeshWeaver.Mesh;

namespace MeshWeaver.Connection.Orleans
{
    public interface IMeshNodeGrain : IGrainWithStringKey
    {
        public Task<MeshNode> Get();
        public Task Update(MeshNode entry);
    }
}

