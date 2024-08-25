using MeshWeaver.Messaging;
using Orleans;

namespace MeshWeaver.Orleans.Contract
{
    public interface IArticleGrain : IGrainWithGuidKey
    {
        public Task<ArticleEntry> Get();
        public Task Update(ArticleEntry entry);
    }
}
