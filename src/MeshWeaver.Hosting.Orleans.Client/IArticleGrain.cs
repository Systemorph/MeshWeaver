using MeshWeaver.Mesh.Contract;

namespace MeshWeaver.Hosting.Orleans.Client
{
    public interface IArticleGrain : IGrainWithStringKey
    {
        public Task<ArticleEntry> Get();
        public Task Update(ArticleEntry entry);
    }
}
