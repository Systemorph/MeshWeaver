using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive.Subjects;
using System.Reflection;
using MeshWeaver.Disposables;
using MeshWeaver.Messaging;
using MeshWeaver.Reflection;
using MeshWeaver.ShortGuid;

namespace MeshWeaver.Data.Serialization;

public record SynchronizationStream<TStream> : ISynchronizationStream<TStream>
{
    /// <summary>
    /// The stream reference, i.e. the unique identifier of the stream.
    /// </summary>
    public StreamIdentity StreamIdentity { get; }

    /// <summary>
    /// The owner of the stream. Changes are to be made as update request to the owner.
    /// </summary>
    public object Owner => StreamIdentity.Owner;

    /// <summary>
    /// The projected reference of the stream, e.g. a collection (CollectionReference),
    /// a layout area (LayoutAreaReference), etc.
    /// </summary>
    public object Reference { get; init; }

    /// <summary>
    /// My current state deserialized as snapshot
    /// </summary>
    private ChangeItem<TStream> current;


    /// <summary>
    /// My current state deserialized as stream
    /// </summary>
    protected readonly ReplaySubject<ChangeItem<TStream>> Store = new(1);

    object ISynchronizationStream.Reference => Reference;

    public ISynchronizationStream<TReduced> Reduce<TReduced>(
        WorkspaceReference<TReduced> reference,
        Func<StreamConfiguration<TReduced>, StreamConfiguration<TReduced>> config
    ) =>
        (ISynchronizationStream<TReduced>)
            ReduceMethod
                .MakeGenericMethod(typeof(TReduced), reference.GetType())
                .Invoke(this, [reference, config]);

    private static readonly MethodInfo ReduceMethod = ReflectionHelper.GetMethodGeneric<
        SynchronizationStream<TStream>
    >(x => x.Reduce<object, WorkspaceReference<object>>(null, null));

    public ISynchronizationStream<TReduced> Reduce<TReduced, TReference2>(
        TReference2 reference)
        where TReference2 : WorkspaceReference =>
        Reduce<TReduced, TReference2>(reference,  x => x);


    public ISynchronizationStream<TReduced> Reduce<TReduced>(WorkspaceReference<TReduced> reference)
        => Reduce(reference,  x => x);


    public ISynchronizationStream<TReduced> Reduce<TReduced, TReference2>(
        TReference2 reference,
        Func<StreamConfiguration<TReduced>, StreamConfiguration<TReduced>> config)
        where TReference2 : WorkspaceReference =>
        ReduceManager.ReduceStream(this, reference, config);

    public virtual IDisposable Subscribe(IObserver<ChangeItem<TStream>> observer)
    {
        try
        {
            return Store.Subscribe(observer);
        }
        catch (ObjectDisposedException)
        {
            return new AnonymousDisposable(() => { });
        }
    }

    private bool isDisposed;
    private readonly object disposeLock = new();


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
        if (current is not null  && Equals(current.Value,value.Value))
            return;
        current = value;
        if (!isDisposed)
            Store.OnNext(value);
    }
    public void UpdateAsync(Func<TStream, ChangeItem<TStream>> update) =>
        InvokeAsync(() => SetCurrent(update.Invoke(Current is null ? default : Current.Value)));
    public void Update(Func<TStream, ChangeItem<TStream>> update) =>
        SetCurrent(update.Invoke(Current is null ? default : Current.Value));

    public void Initialize(Func<CancellationToken, Task<TStream>> init)
    {
        InvokeAsync(async ct => SetCurrent(new ChangeItem<TStream>(await init.Invoke(ct), Hub.Version)));
    }

    public void Initialize(TStream startWith)
    {
        SetCurrent(new ChangeItem<TStream>(startWith, Hub.Version));
    }



    public void OnCompleted()
    {
        Store.OnCompleted();
    }

    public void OnError(Exception error)
    {
        Store.OnError(error);
    }

    public void AddDisposable(IDisposable disposable) => Hub.RegisterForDisposal(_ => disposable.Dispose());

    public IMessageDelivery DeliverMessage(
        IMessageDelivery delivery
    ) =>
        synchronizationHub.DeliverMessage(delivery.ForwardTo(synchronizationHub.Address));


    public void OnNext(ChangeItem<TStream> value)
    {
        InvokeAsync(() => SetCurrent(value));
    }

    public virtual void RequestChange(Func<TStream, ChangeItem<TStream>> update)
    {
        // TODO V10: Here we need to inject validations (29.07.2024, Roland Bürgi)
        UpdateAsync(update);
    }

    public SynchronizationStream(
        StreamIdentity StreamIdentity,
        IMessageHub Hub,
        object Reference,
        ReduceManager<TStream> ReduceManager,
        Func<StreamConfiguration<TStream>, StreamConfiguration<TStream>> configuration)
    {
        this.Hub = Hub;
        this.ReduceManager = ReduceManager;
        this.StreamIdentity = StreamIdentity;
        this.Reference = Reference;
        this.Configuration = configuration?.Invoke(new StreamConfiguration<TStream>(this)) ?? new StreamConfiguration<TStream>(this);
        synchronizationHub = Hub.GetHostedHub(new SynchronizationStreamAddress(StreamId), config => Configuration.HubConfigurations.Aggregate(config,(c,cc) => cc.Invoke(c)));
        synchronizationHub.RegisterForDisposal(_ => Store.Dispose());
    }

    public string StreamId { get; } = Guid.NewGuid().AsString();
    public string ClientId => Configuration.ClientId;


    private StreamConfiguration<TStream> Configuration { get; }

    private readonly IMessageHub synchronizationHub;
    public void InvokeAsync(Action action)
        => synchronizationHub.InvokeAsync(action);

    public void InvokeAsync(Func<CancellationToken, Task> action)
        => synchronizationHub.InvokeAsync(action);



    private record SynchronizationStreamAddress(string Id);


    public void Revert<TReduced>(ChangeItem<TReduced> change)
    {
        // TODO V10: Implement revert mechanism (20.10.2024, Roland Bürgi)
    }

    public void Dispose()
    {
        lock (disposeLock)
        {
            if(isDisposed)
                return;
            isDisposed = true;
        }
        synchronizationHub.Dispose();
    }
    private ConcurrentDictionary<string, object> Properties { get; } = new();
    public T Get<T>(string key) => (T)Properties.GetValueOrDefault(key);
    public T Get<T>() => (T)Properties.GetValueOrDefault(typeof(T).FullName);
    public void Set<T>(string key, T value) => Properties[key] = value;
    public void Set<T>(T value) => Properties[typeof(T).FullName!] = value;

    private readonly ConcurrentDictionary<int, Task> tasks = new();
    public void BindToTask(Task task)
    {
        tasks[task.Id] = task;
        task.ContinueWith(t =>
        {
            tasks.TryRemove(task.Id, out var _);
            if (t is { IsFaulted: true, Exception: not null })
                Store.OnError(t.Exception);
        });
    }
}
public record StreamConfiguration<TStream>(ISynchronizationStream<TStream> Stream)
{
    internal string ClientId { get; init; } //= Guid.NewGuid().AsString();
    public StreamConfiguration<TStream> WithClientId(string streamId) =>
        this with { ClientId = streamId };

    internal ImmutableList<Func<MessageHubConfiguration, MessageHubConfiguration>> HubConfigurations { get; init; } =
        [];
    public StreamConfiguration<TStream> ConfigureHub(Func<MessageHubConfiguration, MessageHubConfiguration> configuration) =>
        this with { HubConfigurations = HubConfigurations.Add(configuration) };

}

