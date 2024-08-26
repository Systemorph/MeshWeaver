using MeshWeaver.Messaging;
using Orleans;

namespace MeshWeaver.Orleans.Client
{
    public interface IMeshNodeGrain : IGrainWithStringKey
    {
        public Task<MeshNode> Get();
        public Task Update(MeshNode entry);
    }
}

