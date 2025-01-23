using MeshWeaver.Articles;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Connection.Orleans;

public interface IArticleGrain : IGrainWithStringKey
{
    public Task Update(Article entry);
    Task<Article> Get(ArticleOptions options);
}
