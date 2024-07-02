using OpenSmc.Messaging;

namespace OpenSmc.Data.Serialization;

public interface ISynchronizationStream : IDisposable
{
    object Owner { get; }
    object Reference { get; }
    object Subscriber { get; }

    internal IMessageDelivery DeliverMessage(IMessageDelivery<WorkspaceMessage> delivery);
    void AddDisposable(IDisposable disposable);

    Task Initialized { get; }
    ISynchronizationStream Reduce(WorkspaceReference reference, object subscriber) =>
        Reduce((dynamic)reference, subscriber);
    ISynchronizationStream<TReduced> Reduce<TReduced>(
        WorkspaceReference<TReduced> reference,
        object subscriber
    );

    ISynchronizationStream<TReduced, TReference2> Reduce<TReduced, TReference2>(
        TReference2 reference,
        object subscriber
    )
        where TReference2 : WorkspaceReference;

    IMessageHub Hub { get; }

    public void Post(WorkspaceMessage message) =>
        Hub.Post(message with { Owner = Owner, Reference = Reference }, o => o.WithTarget(Owner));
}

public interface ISynchronizationStream<TStream>
    : ISynchronizationStream,
        IObservable<ChangeItem<TStream>>,
        IObserver<ChangeItem<TStream>>
{
    ChangeItem<TStream> Current { get; }
    void Update(Func<TStream, ChangeItem<TStream>> update);

    StreamReference StreamReference { get; }
    new Task<TStream> Initialized { get; }
    void Initialize(ChangeItem<TStream> current);

    InitializationMode InitializationMode { get; }
    ReduceManager<TStream> ReduceManager { get; }
    DataChangeResponse RequestChange(Func<TStream, ChangeItem<TStream>> update);
    void NotifyChange(Func<TStream, ChangeItem<TStream>> update);
}

public interface ISynchronizationStream<TStream, out TReference> : ISynchronizationStream<TStream>
{
    new TReference Reference { get; }
}

public enum InitializationMode
{
    Automatic,
    Manual
}
