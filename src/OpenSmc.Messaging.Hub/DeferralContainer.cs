using System.Threading.Tasks.Dataflow;
using OpenSmc.Disposables;

namespace OpenSmc.Messaging.Hub;

public class DeferralContainer : IDisposable
{
    private readonly ActionBlock<IMessageDelivery> executionQueueAction;
    private readonly LinkedList<SyncDelivery> deferralChain = new();
    private readonly List<IDisposable> deferralItems = new();

    public DeferralContainer(ActionBlock<IMessageDelivery> executionQueueAction)
    {
        this.executionQueueAction = executionQueueAction;

        deferralChain.AddLast(d =>
        {
            if (d == null)
                return null;
            executionQueueAction.Post(d);
            return null;
        });
    }

    public IDisposable Defer(Predicate<IMessageDelivery> deferredFilter)
    {
        var deliveryLink = new DeferralItem(deferredFilter, executionQueueAction);
        deferralItems.Add(deliveryLink);
        deferralChain.AddFirst(deliveryLink.DeliverMessage);
        return new AnonymousDisposable(() =>
        {
            deliveryLink.Release();
        });
    }

    public void DeferMessage(IMessageDelivery delivery)
    {
        ExecuteBuffer(delivery, deferralChain.First);
    }

    private void ExecuteBuffer(IMessageDelivery delivery, LinkedListNode<SyncDelivery> node)
    {
        delivery = node.Value(delivery);
        if (delivery == null)
            return;

        if (node.Next == null)
        {
            // TODO V10: Figure out how to do report found (2023/10/07, Roland Buergi)
            return;
        }

        ExecuteBuffer(delivery, node.Next);
    }

    public void Dispose()
    {
        foreach (var deferralItem in deferralItems)
        {
            deferralItem.Dispose();
        }
    }
}