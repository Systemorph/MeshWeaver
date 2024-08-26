using MeshWeaver.Messaging;
using Orleans;

namespace MeshWeaver.Orleans.Client
{
    public interface IArticleGrain : IGrainWithGuidKey
    {
        public Task<ArticleEntry> Get();
        public Task Update(ArticleEntry entry);
    }
}
