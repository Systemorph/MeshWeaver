using OpenSmc.Messaging;

namespace OpenSmc.Data.Serialization;

public interface ISynchronizationStream : IDisposable
{
    object Owner { get; }
    object Subscriber { get; }
    object Reference { get; }
    object RemoteAddress { get; }

    internal IMessageDelivery DeliverMessage(IMessageDelivery<WorkspaceMessage> delivery);
    void AddDisposable(IDisposable disposable);

    Task Initialized { get; }

    IMessageHub Hub { get; }
    public void Post(WorkspaceMessage message) =>
        Hub.Post(message with { Id = Owner, Reference = Reference }, o => o.WithTarget(Owner));
}

public interface ISynchronizationStream<TStream>
    : ISynchronizationStream,
        IObservable<ChangeItem<TStream>>,
        IObserver<ChangeItem<TStream>>
{
    void Update(Func<TStream, ChangeItem<TStream>> update);
    IObservable<IChangeItem> Reduce(WorkspaceReference reference) => Reduce((dynamic)reference);

    ISynchronizationStream<TReduced> Reduce<TReduced>(WorkspaceReference<TReduced> reference);

    new Task<TStream> Initialized { get; }

    ReduceManager<TStream> ReduceManager { get; }
}

public interface ISynchronizationStream<TStream, out TReference> : ISynchronizationStream<TStream>
{
    new TReference Reference { get; }
}
