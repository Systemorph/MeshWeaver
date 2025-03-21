﻿using MeshWeaver.Data.Serialization;
using MeshWeaver.Messaging;

namespace MeshWeaver.Data;

public interface ISynchronizationStream : IDisposable
{
    Address Owner { get; }
    object Reference { get; }
    string StreamId { get; }
    string ClientId { get; }

    StreamIdentity StreamIdentity { get; }
    internal IMessageDelivery DeliverMessage(IMessageDelivery delivery);
    void RegisterForDisposal(IDisposable disposable);

    ISynchronizationStream Reduce(
        WorkspaceReference reference) => Reduce((dynamic)reference);
    ISynchronizationStream<TReduced> Reduce<TReduced>(
        WorkspaceReference<TReduced> reference);

    ISynchronizationStream<TReduced> Reduce<TReduced>(
        WorkspaceReference<TReduced> reference,
        Func<StreamConfiguration<TReduced>, StreamConfiguration<TReduced>> config
    );

    IMessageHub Hub { get; }
    T Get<T>(string key);
    T Get<T>();
    void Set<T>(string key, T value);
    void Set<T>(T value);


}

public interface ISynchronizationStream<TStream>
    : ISynchronizationStream,
        IObservable<ChangeItem<TStream>>,
        IObserver<ChangeItem<TStream>>
{
    ChangeItem<TStream> Current { get; }
    void Update(Func<TStream, ChangeItem<TStream>> update, Action<Exception> exceptionCallback);
    void Update(Func<TStream, CancellationToken, Task<ChangeItem<TStream>>> update, Action<Exception> exceptionCallback);
    void Initialize(Func<CancellationToken, Task<TStream>> init, Action<Exception> exceptionCallback);
    void Initialize(Func<TStream> init, Action<Exception> exceptionCallback) => Initialize(_ => Task.FromResult(init()), exceptionCallback);
    void Initialize(TStream init);
    ReduceManager<TStream> ReduceManager { get; }
    void RequestChange(Func<TStream, ChangeItem<TStream>> update, Action<Exception> exceptionCallback);
    void InvokeAsync(Action action, Action<Exception> exceptionCallback);
    void InvokeAsync(Func<CancellationToken, Task> action, Action<Exception> exceptionCallback);

}


public enum InitializationMode
{
    Automatic,
    Manual
}
