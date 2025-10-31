using System.Threading.Tasks.Dataflow;

namespace MeshWeaver.Messaging;

public record DeferralItem : IAsyncDisposable, IDisposable
{
    private readonly SyncDelivery syncDelivery;
    private readonly SyncDelivery failure;
    private readonly ActionBlock<IMessageDelivery> executionBuffer;
    private readonly BufferBlock<IMessageDelivery> deferral = new();
    private bool isReleased;

    public DeferralItem(Predicate<IMessageDelivery> Filter, SyncDelivery syncDelivery, SyncDelivery failure)
    {
        this.syncDelivery = syncDelivery;
        this.failure = failure;
        executionBuffer = new ActionBlock<IMessageDelivery>(d => syncDelivery(d));
        this.Filter = Filter;
    }

    public IMessageDelivery Failure(
        IMessageDelivery delivery
    )
        => failure.Invoke(delivery);

    public IMessageDelivery DeliverMessage(
        IMessageDelivery delivery
    )
    {
        if (Filter(delivery))
        {
            deferral.Post(delivery);
            return null!;
        }

        try
        {
            // TODO V10: Add logging here. (30.07.2024, Roland Bürgi)
            var ret = syncDelivery.Invoke(delivery);
            if(ret is null)
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
    private readonly object locker = new object();

    public void Dispose()
    {
        bool shouldLink;
        lock (locker)
        {
            if (isLinked)
                return;
            isLinked = true;
            shouldLink = true;
        }

        // Link OUTSIDE the lock to avoid deadlock
        if (shouldLink)
            deferral.LinkTo(executionBuffer, new DataflowLinkOptions { PropagateCompletion = true });
    }

    public void Release()
    {
        bool shouldLink;
        lock (locker)
        {
            if (isReleased)
                return;
            isReleased = true;
            shouldLink = true;
        }

        // Link OUTSIDE the lock to avoid deadlock
        if (shouldLink)
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
