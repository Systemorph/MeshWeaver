using System.Collections.Concurrent;
using System.Reactive.Subjects;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeshWeaver.Messaging;
using MeshWeaver.Reflection;
using MeshWeaver.ShortGuid;
using MeshWeaver.Utils;
using Activity = MeshWeaver.Activities.Activity;

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

    public ISynchronizationStream<TReduced>? Reduce<TReduced>(
        WorkspaceReference<TReduced> reference,
        Func<StreamConfiguration<TReduced>, StreamConfiguration<TReduced>>? config
    ) =>
        (ISynchronizationStream<TReduced>?)
            ReduceMethod
                .MakeGenericMethod(typeof(TReduced), reference.GetType())
                .Invoke(this, [reference, config]);

    private static readonly MethodInfo ReduceMethod = ReflectionHelper.GetMethodGeneric<
        SynchronizationStream<TStream>
    >(x => x.Reduce<object, WorkspaceReference<object>>(null!, null!));

    public ISynchronizationStream<TReduced> Reduce<TReduced, TReference2>(
        TReference2 reference)
        where TReference2 : WorkspaceReference =>
        Reduce<TReduced, TReference2>(reference, x => x);


    public ISynchronizationStream<TReduced>? Reduce<TReduced>(WorkspaceReference<TReduced> reference)
        => Reduce(reference, (Func<StreamConfiguration<TReduced>, StreamConfiguration<TReduced>>?)(x => x));


    public ISynchronizationStream<TReduced> Reduce<TReduced, TReference2>(
        TReference2 reference,
        Func<StreamConfiguration<TReduced>, StreamConfiguration<TReduced>> config)
        where TReference2 : WorkspaceReference =>
        ReduceManager.ReduceStream(this, reference, config) ?? throw new InvalidOperationException("Failed to create reduced stream");

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


    public ChangeItem<TStream>? Current
    {
        get => current;
    }


    /// <summary>
    /// The actual synchronization hub
    /// </summary>
    public IMessageHub Hub { get; }

    /// <summary>
    /// The host of the synchronization stream.
    /// </summary>
    public IMessageHub Host { get; }



    public ReduceManager<TStream> ReduceManager { get; init; }

    private void SetCurrent(ChangeItem<TStream>? value)
    {
        if (startupDeferrable is not null)
        {
            startupDeferrable.Dispose();
            startupDeferrable = null;
        }
        if (isDisposed || value == null)
            return;
        if (current is not null && Equals(current.Value, value.Value))
            return;
        current = value;
        if (!isDisposed)
            Store.OnNext(value);
    }

    public void Update(Func<TStream?, ChangeItem<TStream>?> update, Func<Exception, Task> exceptionCallback) =>
        Hub.Post(new UpdateStreamRequest((stream, _) => Task.FromResult(update.Invoke(stream)), exceptionCallback));

    public void Update(Func<TStream?, CancellationToken, Task<ChangeItem<TStream>?>> update,
        Func<Exception, Task> exceptionCallback) =>
        Hub.Post(new UpdateStreamRequest(update, exceptionCallback));

    public void Initialize(Func<CancellationToken, Task<TStream>> init, Func<Exception, Task> exceptionCallback)
    {
        InvokeAsync(async ct => SetCurrent(new ChangeItem<TStream>(await init.Invoke(ct), StreamId, Hub.Version)), exceptionCallback);
    }

    public void Initialize(TStream startWith)
    {
        SetCurrent(new ChangeItem<TStream>(startWith, StreamId, Hub.Version));
    }



    public void OnCompleted()
    {
        Store.OnCompleted();
    }

    public void OnError(Exception error)
    {
        Store.OnError(error);
    }

    public void RegisterForDisposal(IDisposable disposable) => Hub
        .RegisterForDisposal(_ => disposable.Dispose());

    public IMessageDelivery DeliverMessage(IMessageDelivery delivery) =>
        Hub.DeliverMessage(delivery.ForwardTo(Hub.Address));


    public void OnNext(ChangeItem<TStream> value)
    {
        InvokeAsync(() => SetCurrent(value), ex => throw new SynchronizationException("An error occurred during synchronization", ex));
    }

    public virtual void RequestChange(Func<TStream?, ChangeItem<TStream>?> update, Func<Exception, Task> exceptionCallback)
    {
        // TODO V10: Here we need to inject validations (29.07.2024, Roland Bürgi)
        Update(update, exceptionCallback);
    }

    public SynchronizationStream(
        StreamIdentity StreamIdentity,
        IMessageHub Host,
        object Reference,
        ReduceManager<TStream> ReduceManager,
        Func<StreamConfiguration<TStream>, StreamConfiguration<TStream>>? configuration)
    {
        if (Host.IsDisposing)
            throw new ObjectDisposedException($"Hub {Host.Address} is disposing. Cannot create synchronization stream.");
        this.Host = Host;

        this.Configuration = configuration?.Invoke(new StreamConfiguration<TStream>(this)) ?? new StreamConfiguration<TStream>(this);


        this.Hub = Host.GetHostedHub(new SynchronizationAddress(ClientId),
            ConfigureSynchronizationHub);

        startupDeferrable = Hub.Defer(d => d.Message is not DataChangedEvent && d.Message is not ExecutionRequest);

        this.ReduceManager = ReduceManager;
        this.StreamIdentity = StreamIdentity;
        this.Reference = Reference;

        Hub.RegisterForDisposal(_ => Store.Dispose());
    }

    private IDisposable? startupDeferrable;

    private MessageHubConfiguration ConfigureSynchronizationHub(MessageHubConfiguration config)
    {
        return config
            .WithTypes(
                typeof(EntityStore),
                typeof(JsonElement),
                typeof(SynchronizationAddress)
            )
            .WithHandler<DataChangedEvent>((hub, delivery) =>
                {
                    UpdateStream(delivery, hub);
                    return delivery.Processed();
                }
            ).WithHandler<PatchDataChangeRequest>((hub, delivery) =>
                {
                    UpdateStream(delivery, hub);
                    return delivery.Processed();
                }
            ).WithHandler<DataChangeRequest>((hub, delivery) =>
                {
                    hub.GetWorkspace().RequestChange(delivery.Message, new Activity(ActivityCategory.DataUpdate, Host),
                        delivery);
                    return delivery.Processed();
                }
            ).WithHandler<UnsubscribeRequest>((hub, delivery) =>
            {
                hub.Dispose();
                return delivery.Processed();
            }).WithHandler<UpdateStreamRequest>(async (_, request, ct) =>
            {
                var update = request.Message.UpdateAsync;
                var exceptionCallback = request.Message.ExceptionCallback;
                try
                {
                    SetCurrent(await update.Invoke(Current is null ? default : Current.Value, ct));
                }
                catch (Exception e)
                {
                    await exceptionCallback.Invoke(e);
                }
                return request.Processed();
            });

    }


    private void UpdateStream<TChange>(IMessageDelivery<TChange> delivery, IMessageHub hub)
        where TChange : JsonChange
    {
        var currentJson = Get<JsonElement?>();
        if (delivery.Message.ChangeType == ChangeType.Full)
        {
            currentJson = JsonSerializer.Deserialize<JsonElement>(delivery.Message.Change.Content);
            try
            {
                SetCurrent(new ChangeItem<TStream>(
                    currentJson.Value.Deserialize<TStream>(Host.JsonSerializerOptions)!,
                    StreamId,
                    Host.Version));

            }
            catch (Exception ex)
            {
                SyncFailed(delivery, ex);
            }

        }
        else
        {
            (currentJson, var patch) = delivery.Message.UpdateJsonElement(currentJson, hub.JsonSerializerOptions);
            try
            {
                SetCurrent(this.ToChangeItem(Current!.Value!,
                    currentJson.Value,
                    patch,
                    delivery.Message.ChangedBy ?? ClientId));

            }
            catch (Exception ex)
            {
                SyncFailed(delivery, ex);
            }

        }
        Set(currentJson);
    }

    private Task SyncFailed(IMessageDelivery delivery, Exception exception)
    {
        Host.Post(new DeliveryFailure(delivery, exception.Message), o => o.ResponseFor(delivery));
        return Task.CompletedTask;
    }



    public string StreamId { get; } = Guid.NewGuid().AsString();
    public string ClientId => Configuration.ClientId;


    internal StreamConfiguration<TStream> Configuration { get; }

    public void InvokeAsync(Action action, Func<Exception, Task> exceptionCallback)
        => Hub.InvokeAsync(action, exceptionCallback);

    public void InvokeAsync(Func<CancellationToken, Task> action, Func<Exception, Task> exceptionCallback)
        => Hub.InvokeAsync(action, exceptionCallback);


    public void Dispose()
    {
        lock (disposeLock)
        {
            if (isDisposed)
                return;
            isDisposed = true;
        }
        Hub.Dispose();
    }
    private ConcurrentDictionary<string, object?> Properties { get; } = new();
    public T? Get<T>(string key) => (T?)Properties.GetValueOrDefault(key);
    public T? Get<T>() => Get<T>(typeof(T).FullName!);
    public void Set<T>(string key, T? value) => Properties[key] = value;
    public void Set<T>(T? value) => Properties[typeof(T).FullName!] = value;

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

    public record UpdateStreamRequest([property: JsonIgnore] Func<TStream?, CancellationToken, Task<ChangeItem<TStream>?>> UpdateAsync, [property: JsonIgnore] Func<Exception, Task> ExceptionCallback);

}


public record StreamConfiguration<TStream>(ISynchronizationStream<TStream> Stream)
{
    internal string ClientId { get; init; } = Guid.NewGuid().AsString();
    public StreamConfiguration<TStream> WithClientId(string streamId) =>
        this with { ClientId = streamId };

    internal bool NullReturn { get; init; }

    public StreamConfiguration<TStream> ReturnNullWhenNotPresent()
        => this with { NullReturn = true };

}
