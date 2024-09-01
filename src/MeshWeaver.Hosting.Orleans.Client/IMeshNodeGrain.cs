using MeshWeaver.Mesh.Contract;

namespace MeshWeaver.Hosting.Orleans.Client
{
    public interface IMeshNodeGrain : IGrainWithStringKey
    {
        public Task<MeshNode> Get();
        public Task Update(MeshNode entry);
    }
}

