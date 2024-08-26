using MeshWeaver.Messaging;
using Orleans;

namespace MeshWeaver.Orleans.Client
{
    public interface IMeshCatalogGrain : IGrainWithStringKey
    {
        Task<MeshNode> GetEntry();
        Task Update(MeshNode node);
    }
}
