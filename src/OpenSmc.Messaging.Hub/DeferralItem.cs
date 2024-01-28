using System.Threading.Tasks.Dataflow;

namespace OpenSmc.Messaging;

public record DeferralItem : IDisposable
{
    private readonly ITargetBlock<IMessageDelivery> executionBuffer;
    private readonly BufferBlock<IMessageDelivery> deferral = new();
    private bool isReleased;

    public DeferralItem(Predicate<IMessageDelivery> Filter, ITargetBlock<IMessageDelivery> executionBuffer)
    {
        this.executionBuffer = executionBuffer;
        this.Filter = Filter;
    }

    public IMessageDelivery DeliverMessage(IMessageDelivery delivery)
    {
        if (Filter(delivery))
        {
            deferral.Post(delivery);
            return null;
        }

        return delivery;
    }

    public void Dispose()
    {
        deferral.Complete();
    }

    public void Release()
    {
        lock (this)
        {
            if (isReleased)
                return;
            isReleased = true;
        }
        deferral.LinkTo(executionBuffer);
    }

    public Predicate<IMessageDelivery> Filter { get; init; }

}