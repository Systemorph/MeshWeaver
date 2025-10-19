using System.Threading.Tasks.Dataflow;

namespace MeshWeaver.Messaging;

public record DeferralItem : IAsyncDisposable, IDisposable
{
    private readonly AsyncDelivery asyncDelivery;
    private readonly SyncDelivery failure;
    private readonly ActionBlock<(IMessageDelivery Delivery, CancellationToken CancellationToken)> executionBuffer;
    private readonly BufferBlock<(IMessageDelivery Delivery, CancellationToken CancellationToken)> deferral = new();
    private readonly CancellationTokenSource cancellationTokenSource = new();
    private bool isReleased;

    public DeferralItem(Predicate<IMessageDelivery> Filter, AsyncDelivery asyncDelivery, SyncDelivery failure)
    {
        this.asyncDelivery = asyncDelivery;
        this.failure = failure;
        executionBuffer = new ActionBlock<(IMessageDelivery Delivery, CancellationToken CancellationToken)>(async tup => await asyncDelivery(tup.Delivery, tup.CancellationToken));
        this.Filter = Filter;
    }

    public IMessageDelivery Failure(
        IMessageDelivery delivery
    )
        => failure.Invoke(delivery);

    public async Task<IMessageDelivery> DeliverMessage(
        IMessageDelivery delivery,
        CancellationToken cancellationToken
    )
    {
        if (Filter(delivery))
        {
            deferral.Post((delivery, cancellationToken));
            return null!;
        }

        try
        {
            // TODO V10: Add logging here. (30.07.2024, Roland Bürgi)
            var ret = await asyncDelivery.Invoke(delivery, cancellationToken);
            if (ret is null)
                return null!;
            if (ret.State == MessageDeliveryState.Failed)
                return failure(ret);
            return ret;
        }
        catch (Exception e)
        {
            // TODO V10: Add logging here. (30.07.2024, Roland Bürgi)
            var ret = delivery.Failed(e.Message);
            failure.Invoke(ret);
            return ret;
        }
    }

    private bool isLinked;
    private readonly object locker = new();

    public void Dispose()
    {
        if (isLinked)
            return;
        isLinked = true;
        deferral.LinkTo(executionBuffer, new DataflowLinkOptions { PropagateCompletion = true });
    }

    public void Release()
    {
        lock (locker)
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
