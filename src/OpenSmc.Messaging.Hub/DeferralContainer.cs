using OpenSmc.Disposables;

namespace OpenSmc.Messaging;

public class DeferralContainer : IAsyncDisposable
{
    private readonly LinkedList<DeferralItem> deferralChain = new();

    public DeferralContainer(AsyncDelivery asyncDelivery)
    {
        deferralChain.AddLast(new DeferralItem(_ => false, asyncDelivery));
    }

    public IDisposable Defer(Predicate<IMessageDelivery> deferredFilter)
    {
        var deferralItem = deferralChain.First;

        var deliveryLink = new DeferralItem(deferredFilter, deferralItem!.Value.DeliverMessage);
        deferralChain.AddFirst(deliveryLink);
        return new AnonymousDisposable(() =>
        {
            deliveryLink.Release();
        });
    }

    public Task<IMessageDelivery> DeliverAsync(IMessageDelivery delivery) =>
        deferralChain.First!.Value.DeliverMessage(delivery);

    public async ValueTask DisposeAsync()
    {
        foreach (var deferralItem in deferralChain)
            await deferralItem.DisposeAsync();
    }
}