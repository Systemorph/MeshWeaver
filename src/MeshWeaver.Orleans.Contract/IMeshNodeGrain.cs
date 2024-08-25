using MeshWeaver.Messaging;
using Orleans;

namespace MeshWeaver.Orleans.Contract
{
    public interface IMeshNodeGrain : IGrainWithStringKey
    {
        public Task<MeshNode> Get();
        public Task Update(MeshNode entry);
    }
}

