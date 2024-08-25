using MeshWeaver.Messaging;
using Orleans;

namespace MeshWeaver.Orleans.Contract;

public interface IMeshNodeGrain : IGrainWithStringKey
{
    public Task<MeshNode> Get();
    public Task Update(MeshNode entry);
}

public interface IArticleGrain : IGrainWithGuidKey
{
    public Task<ArticleEntry> Get();
    public Task Update(ArticleEntry entry);
}

public interface IRoutingGrain : IGrainWithIntegerKey
{

}

public interface IMessageHubGrain : IGrainWithGuidKey
{
}
