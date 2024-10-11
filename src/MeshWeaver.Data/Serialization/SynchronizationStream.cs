﻿using System.Collections.Immutable;
using System.Reactive.Subjects;
using System.Reflection;
using MeshWeaver.Disposables;
using MeshWeaver.Messaging;
using MeshWeaver.Reflection;

namespace MeshWeaver.Data.Serialization;

public record SynchronizationStream<TStream, TReference> : ISynchronizationStream<TStream, TReference>
    where TReference : WorkspaceReference
{
    /// <summary>
    /// The stream reference, i.e. the unique identifier of the stream.
    /// </summary>
    public StreamReference StreamReference { get; }

    /// <summary>
    /// The subscriber of the stream, e.g. the Hub Address or Id of the subscriber.
    /// </summary>
    public object Subscriber { get; init; }

    /// <summary>
    /// The projected reference of the stream, e.g. a collection (CollectionReference),
    /// a layout area (LayoutAreaReference), etc.
    /// </summary>
    public TReference Reference { get; init; }

    /// <summary>
    /// My current state deserialized as snapshot
    /// </summary>
    private ChangeItem<TStream> current;


    /// <summary>
    /// My current state deserialized as stream
    /// </summary>
    protected readonly ReplaySubject<ChangeItem<TStream>> Store = new(1);

    private readonly ImmutableArray<(
        Func<IMessageDelivery<WorkspaceMessage>, bool> Applies,
        Func<IMessageDelivery<WorkspaceMessage>, IMessageDelivery> Process
    )> messageHandlers = ImmutableArray<(
        Func<IMessageDelivery<WorkspaceMessage>, bool> Applies,
        Func<IMessageDelivery<WorkspaceMessage>, IMessageDelivery> Process
    )>.Empty;

    private readonly TaskCompletionSource<TStream> initialized = new();


    public Task<TStream> Initialized => initialized.Task;

    public InitializationMode InitializationMode { get; }

    Task ISynchronizationStream.Initialized => initialized.Task;

    object ISynchronizationStream.Reference => Reference;

    public ISynchronizationStream<TReduced> Reduce<TReduced>(
        WorkspaceReference<TReduced> reference,
        object subscriber
    ) =>
        (ISynchronizationStream<TReduced>)
            ReduceMethod
                .MakeGenericMethod(typeof(TReduced), reference.GetType())
                .Invoke(this, [reference, subscriber]);

    private static readonly MethodInfo ReduceMethod = ReflectionHelper.GetMethodGeneric<
        SynchronizationStream<TStream, TReference>
    >(x => x.Reduce<object, WorkspaceReference<object>>(null, null));

    public ISynchronizationStream<TReduced, TReference2> Reduce<TReduced, TReference2>(
        TReference2 reference,
        object subscriber
    )
        where TReference2 : WorkspaceReference =>
        ReduceManager.ReduceStream<TReduced, TReference2>(this, reference, subscriber);

    public virtual IDisposable Subscribe(IObserver<ChangeItem<TStream>> observer)
    {
        try
        {
            return Store.Subscribe(observer);
        }
        catch (ObjectDisposedException)
        {
            return new AnonymousDisposable(() => {});
        }
    }

    public readonly List<IDisposable> Disposables = new();

    private bool isDisposed;
    private readonly object disposeLock = new();

    public void Dispose()
    {
        lock (disposeLock)
        {
            if (isDisposed)
                return;
            isDisposed = true;
        }
        foreach (var disposeAction in Disposables)
            disposeAction.Dispose();

        Store.Dispose();
    }

    public ChangeItem<TStream> Current
    {
        get => current;
    }

    public IMessageHub Hub { get; init; }
    public ReduceManager<TStream> ReduceManager { get; init; }

    private void SetCurrent(ChangeItem<TStream> value)
    {
        if (isDisposed || value == null)
            return;
        if(!IsInitialized)
            initialized.SetResult(value.Value);
        current = value;
        if (!isDisposed)
            Store.OnNext(value);
    }

    private bool IsInitialized => initialized.Task.IsCompleted;

    public virtual void Initialize(ChangeItem<TStream> initial)
    {
        if (Current is not  null)
           throw new InvalidOperationException("Already initialized");

        current = initial ?? throw new ArgumentNullException(nameof(initial));
        Store.OnNext(initial);
        initialized.SetResult(current.Value);
    }

    public void OnCompleted()
    {
        Store.OnCompleted();
    }

    public void OnError(Exception error)
    {
        Store.OnError(error);
    }

    public void AddDisposable(IDisposable disposable) => Disposables.Add(disposable);

    IMessageDelivery ISynchronizationStream.DeliverMessage(
        IMessageDelivery<WorkspaceMessage> delivery
    )
    {
        try
        {
            return messageHandlers
                .Where(x => x.Applies(delivery))
                .Select(x => x.Process)
                .FirstOrDefault()
                ?.Invoke(delivery) ?? delivery;
        }
        catch (Exception e)
        {
            return delivery.Failed($"Exception during execution: {e}");
        }
    }

    public void Update(Func<TStream, ChangeItem<TStream>> update) => 
        InvokeAsync(() => SetCurrent(update.Invoke(Current is null ? default : Current.Value)));

    public void OnNext(ChangeItem<TStream> value)
    {
        if(!IsInitialized)
            Initialize(value);
        else
            InvokeAsync(() => SetCurrent(value));
    }

    public virtual void RequestChange(Func<TStream, ChangeItem<TStream>> update)
    {
        // TODO V10: Here we need to inject validations (29.07.2024, Roland Bürgi)
        Update(update);
    }


    public SynchronizationStream(
        StreamReference StreamReference,
        object Subscriber,
        IMessageHub Hub,
        TReference Reference,
        ReduceManager<TStream> ReduceManager,
        InitializationMode InitializationMode)
    {
        this.Hub = Hub;
        this.ReduceManager = ReduceManager;
        this.StreamReference = StreamReference;
        this.Subscriber = Subscriber;
        this.Reference = Reference;
        this.InitializationMode = InitializationMode;
        synchronizationStreamHub = Hub.GetHostedHub(new SynchronizationStreamAddress(Hub.Address));
    }







    private record SynchronizationStreamAddress(object Host) : IHostedAddress
    {
        /// <summary>
        /// This id is not meant to be accessed.
        /// Rather, it brings uniqueness to multiple instances.
        /// </summary>
        // ReSharper disable once UnusedMember.Local
        public Guid Id { get; init; } = Guid.NewGuid();
    }

    private readonly IMessageHub synchronizationStreamHub;

    //private void InvokeAsync(Func<CancellationToken, Task> task)
    //    => synchronizationStreamHub.InvokeAsync(task);
    private void InvokeAsync(Action action)
        => synchronizationStreamHub.InvokeAsync(action);

}
