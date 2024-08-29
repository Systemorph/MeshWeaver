using MeshWeaver.Messaging;
using Orleans;

namespace MeshWeaver.Orleans.Client
{
    public interface IArticleGrain : IGrainWithStringKey
    {
        public Task<ArticleEntry> Get();
        public Task Update(ArticleEntry entry);
    }
}
