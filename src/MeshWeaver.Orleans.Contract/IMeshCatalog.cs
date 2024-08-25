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

public interface IRoutingGrain : IGrainWithStringKey
{
    Task<IMessageDelivery> DeliverMessage(object routeAddress, IMessageDelivery request);
}

public interface IMessageHubGrain : IGrainWithStringKey
{
}
