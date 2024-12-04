using MeshWeaver.Mesh;

namespace MeshWeaver.Connection.Orleans;

public interface IArticleGrain : IGrainWithStringKey
{
    public Task<MeshArticle> Get(bool includeContent);
    public Task Update(MeshArticle entry);
}
