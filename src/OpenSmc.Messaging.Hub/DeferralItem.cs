using System.Threading.Tasks.Dataflow;

namespace OpenSmc.Messaging;

public record DeferralItem : IAsyncDisposable, IDisposable
{
    private readonly AsyncDelivery asyncDelivery;
    private readonly ActionBlock<(IMessageDelivery Delivery, CancellationToken CancellationToken)> executionBuffer;
    private readonly BufferBlock<(IMessageDelivery Delivery, CancellationToken CancellationToken)> deferral = new();
    private bool isReleased;

    public DeferralItem(Predicate<IMessageDelivery> Filter, AsyncDelivery asyncDelivery)
    {
        this.asyncDelivery = asyncDelivery;
        executionBuffer = new ActionBlock<(IMessageDelivery Delivery, CancellationToken CancellationToken)>(d => asyncDelivery(d.Delivery, d.CancellationToken));
        this.Filter = Filter;
    }

    public async Task<IMessageDelivery> DeliverMessage(IMessageDelivery delivery, CancellationToken cancellationToken)
    {
        if (Filter(delivery))
        {
            deferral.Post((delivery, cancellationToken));
            return null;
        }

        return await asyncDelivery.Invoke(delivery, cancellationToken);
    }

    private bool isLinked;
    public void Dispose()
    {
        if (isLinked)
            return;
        isLinked = true;
        deferral.LinkTo(executionBuffer, new DataflowLinkOptions{PropagateCompletion = true});
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

    public async ValueTask DisposeAsync()
    {
        Dispose();
        deferral.Complete();
        await executionBuffer.Completion;
    }
    public Predicate<IMessageDelivery> Filter { get; init; }

}