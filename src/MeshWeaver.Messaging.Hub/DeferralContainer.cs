using MeshWeaver.Disposables;

namespace MeshWeaver.Messaging;

public class DeferralContainer : IAsyncDisposable
{
    private readonly LinkedList<DeferralItem> deferralChain = new();

    public DeferralContainer(AsyncDelivery asyncDelivery, SyncDelivery failure)
    {
        deferralChain.AddFirst(new DeferralItem(_ => false, asyncDelivery, failure));
    }

    public IDisposable Defer(Predicate<IMessageDelivery> deferredFilter)
    {
        var deferralItem = deferralChain.First;

        var deliveryLink = new DeferralItem(deferredFilter, deferralItem!.Value.DeliverMessage, deferralItem!.Value.Failure);
        deferralChain.AddFirst(deliveryLink);
        return new AnonymousDisposable(deliveryLink.Release);
    }

    public Task<IMessageDelivery> DeliverAsync(IMessageDelivery delivery, CancellationToken token) =>
        deferralChain.First!.Value.DeliverMessage(delivery, token);

    public async ValueTask DisposeAsync()
    {
        foreach (var deferralItem in deferralChain)
            await deferralItem.DisposeAsync();
    }
}
