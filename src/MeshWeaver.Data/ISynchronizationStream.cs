using MeshWeaver.Data.Serialization;
using MeshWeaver.Messaging;

namespace MeshWeaver.Data;

public interface ISynchronizationStream : IAsyncDisposable
{
    object Owner { get; }
    object Reference { get; }
    object Subscriber { get; }
    StreamIdentity StreamIdentity { get; }
    internal IMessageDelivery DeliverMessage(IMessageDelivery delivery);
    void AddDisposable(IDisposable disposable);
    void AddDisposable(IAsyncDisposable disposable);

    ISynchronizationStream Reduce(WorkspaceReference reference, object subscriber = null) =>
        Reduce((dynamic)reference, subscriber);
    ISynchronizationStream<TReduced> Reduce<TReduced>(
        WorkspaceReference<TReduced> reference,
        object subscriber
    );

    ISynchronizationStream<TReduced> Reduce<TReduced, TReference2>(
        TReference2 reference,
        object subscriber,
        Func<StreamConfiguration<TReduced>, StreamConfiguration<TReduced>> config
    )
        where TReference2 : WorkspaceReference;

    IMessageHub Hub { get; }

}

public interface ISynchronizationStream<TStream>
    : ISynchronizationStream,
        IObservable<ChangeItem<TStream>>,
        IObserver<ChangeItem<TStream>>
{
    ChangeItem<TStream> Current { get; }
    void UpdateAsync(Func<TStream, ChangeItem<TStream>> update);
    void Update(Func<TStream, ChangeItem<TStream>> update);
    void Initialize(Func<CancellationToken, Task<TStream>> init);
    void Initialize(TStream init);
    ReduceManager<TStream> ReduceManager { get; }
    string StreamId { get; }
    void RequestChange(Func<TStream, ChangeItem<TStream>> update);
    void InvokeAsync(Action action);
    void InvokeAsync(Func<CancellationToken, Task> action);
    internal IMessageHub SynchronizationHub { get; }

}


public enum InitializationMode
{
    Automatic,
    Manual
}
