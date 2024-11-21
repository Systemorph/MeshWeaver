using MeshWeaver.Mesh.Contract;

namespace MeshWeaver.Hosting.Orleans.Client;

public interface IArticleGrain : IGrainWithStringKey
{
    public Task<MeshArticle> Get(bool includeContent);
    public Task Update(MeshArticle entry);
}
