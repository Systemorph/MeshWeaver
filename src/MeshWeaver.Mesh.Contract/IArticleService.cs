using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Contract;

public interface IArticleService
{
    public Task<ArticleEntry> GetAsync(string id);
    public Task UpdateAsync(ArticleEntry id);
}
