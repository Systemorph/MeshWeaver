using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive.Subjects;
using System.Reflection;
using System.Text.Json;
using MeshWeaver.Messaging;
using MeshWeaver.Reflection;
using MeshWeaver.ShortGuid;
using MeshWeaver.Utils;

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
    public Address Owner => StreamIdentity.Owner;

    /// <summary>
    /// The projected reference of the stream, e.g. a collection (CollectionReference),
    /// a layout area (LayoutAreaReference), etc.
    /// </summary>
    public object Reference { get; init; }

    /// <summary>
    /// My current state deserialized as snapshot
    /// </summary>
    private ChangeItem<TStream>? current;


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
        Reduce<TReduced, TReference2>(reference, x => x);


    public ISynchronizationStream<TReduced> Reduce<TReduced>(WorkspaceReference<TReduced> reference)
        => Reduce(reference, x => x);


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
        if (current is not null && Equals(current.Value, value.Value))
            return;
        current = value;
        if (!isDisposed)
            Store.OnNext(value);
    }
    public void Update(Func<TStream, ChangeItem<TStream>> update, Func<Exception, Task> exceptionCallback) =>
        InvokeAsync(() => SetCurrent(update.Invoke(Current is null ? default : Current.Value)), exceptionCallback);
    public void Update(Func<TStream, CancellationToken, Task<ChangeItem<TStream>>> update, Func<Exception, Task> exceptionCallback) =>
        InvokeAsync(async ct => SetCurrent(await update.Invoke(Current is null ? default : Current.Value, ct)), exceptionCallback);

    public void Initialize(Func<CancellationToken, Task<TStream>> init, Func<Exception, Task> exceptionCallback)
    {
        InvokeAsync(async ct => SetCurrent(new ChangeItem<TStream>(await init.Invoke(ct), Hub.Version)), exceptionCallback);
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

    public void RegisterForDisposal(IDisposable disposable) => synchronizationHub
        .RegisterForDisposal(_ => disposable.Dispose());

    public IMessageDelivery DeliverMessage(IMessageDelivery delivery) =>
        synchronizationHub.DeliverMessage(delivery.ForwardTo(synchronizationHub.Address));


    public void OnNext(ChangeItem<TStream> value)
    {
        InvokeAsync(() => SetCurrent(value), ex => throw new SynchronizationException("An error occurred during synchronization", ex));
    }

    public virtual void RequestChange(Func<TStream, ChangeItem<TStream>> update, Func<Exception, Task> exceptionCallback)
    {
        // TODO V10: Here we need to inject validations (29.07.2024, Roland Bürgi)
        Update(update, exceptionCallback);
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
        synchronizationHub = Hub.GetHostedHub(new SynchronizationStreamAddress($"{Hub.Address}/{StreamId}"), config =>
            Configuration.HubConfigurations.Aggregate(ConfigureDefaults(config), (c, cc) => cc.Invoke(c)));

        if (synchronizationHub == null)
            if (Hub.IsDisposing)
                throw new ObjectDisposedException($"Hub {Hub.Address} is disposing. Cannot create synchronization stream.");
            else
                throw new InvalidOperationException("Could not create synchronization hub");

        synchronizationHub.RegisterForDisposal(_ => Store.Dispose());
    }

    private static MessageHubConfiguration ConfigureDefaults(MessageHubConfiguration config)
        => config.WithTypes(
                typeof(EntityStore),
                typeof(JsonElement),
                typeof(SynchronizationStreamAddress)
            );

    public string StreamId { get; } = Guid.NewGuid().AsString();
    public string ClientId => Configuration.ClientId;


    internal StreamConfiguration<TStream> Configuration { get; }

    private readonly IMessageHub synchronizationHub;
    public void InvokeAsync(Action action, Func<Exception, Task> exceptionCallback)
        => synchronizationHub.InvokeAsync(action, exceptionCallback);

    public void InvokeAsync(Func<CancellationToken, Task> action, Func<Exception, Task> exceptionCallback)
        => synchronizationHub.InvokeAsync(action, exceptionCallback);



    private record SynchronizationStreamAddress(string Id) : Address("sync", Id);


    public void Dispose()
    {
        lock (disposeLock)
        {
            if (isDisposed)
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
    internal string? ClientId { get; init; } //= Guid.NewGuid().AsString();
    public StreamConfiguration<TStream> WithClientId(string streamId) =>
        this with { ClientId = streamId };

    internal ImmutableList<Func<MessageHubConfiguration, MessageHubConfiguration>> HubConfigurations { get; init; } =
        [];
    public StreamConfiguration<TStream> ConfigureHub(Func<MessageHubConfiguration, MessageHubConfiguration> configuration) =>
        this with { HubConfigurations = HubConfigurations.Add(configuration) };

    internal bool NullReturn { get; init; }

    public StreamConfiguration<TStream> ReturnNullWhenNotPresent()
        => this with { NullReturn = true };

}
