using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reflection;
using System.Text.Json;
using Json.More;
using Json.Patch;
using OpenSmc.Messaging;
using OpenSmc.Reflection;

namespace OpenSmc.Data.Serialization;

public interface IChangeStream : IDisposable
{
    object Id { get; }
    object Reference { get; }

    internal IMessageDelivery DeliverMessage(IMessageDelivery<WorkspaceMessage> delivery);
    void AddDisposable(IDisposable disposable);

    Task Initialized { get; }

    IMessageHub Hub { get; }
    public void Post(WorkspaceMessage message) =>
        Hub.Post(message with { Id = Id, Reference = Reference }, o => o.WithTarget(Id));
    IObservable<DataChangedEvent> DataChanged { get; }
    IObservable<DataChangedEvent> DataSynchronization { get; }
    DataChangeResponse RequestChange(DataChangedEvent request, object changedBy);

}

public interface IChangeStream<TStream>
    : IChangeStream,
        IObservable<ChangeItem<TStream>>,
        IObserver<ChangeItem<TStream>>
{
    void Update(Func<TStream, ChangeItem<TStream>> update);
    void Initialize(TStream value);
    IObservable<IChangeItem> Reduce(WorkspaceReference reference)
        => Reduce((dynamic)reference);

    IChangeStream<TReduced> Reduce<TReduced>(WorkspaceReference<TReduced> reference);

    new Task<TStream> Initialized { get; }

    ReduceManager<TStream> ReduceManager { get; }
}

public interface IChangeStream<TStream, out TReference> : IChangeStream<TStream>
{
    ChangeItem<TStream> Current { get; }
    new TReference Reference { get; }
    void NotifyChange(DataChangedEvent deliveryMessage);
}


public record ChangeStream<TStream, TReference> : IChangeStream<TStream, TReference> where TReference : WorkspaceReference
{
    /// <summary>
    /// Id of the stream, e.g. the Hub Address or Id of datasource.
    /// </summary>
    public object Id { get; init; }

    /// <summary>
    /// The projected reference of the stream, e.g. a collection (CollectionReference),
    /// a layout area (LayoutAreaReference), etc.
    /// </summary>
    public TReference Reference { get; init; }

    public void NotifyChange(DataChangedEvent request)
    {
        if (!initialized.Task.IsCompleted)
        {
            Initialize(GetFullState(request));
        }
        else
            Update(request);

    }

    private TStream GetFullState(DataChangedEvent request)
    {
        return JsonSerializer.Deserialize<TStream>(request.Change.Content, Hub.JsonSerializerOptions);
    }

    private void Update(DataChangedEvent request)
    {
        var newState = request.ChangeType switch
        {
            ChangeType.Patch => ApplyPatch(request),
            ChangeType.Full => GetFullState(request),
            _ => throw new ArgumentOutOfRangeException()
        };
        store.OnNext(new(Id, Reference, newState, request.ChangedBy, request.Version));
    }

    private TStream ApplyPatch(DataChangedEvent request)
    {
        var patch = JsonSerializer.Deserialize<JsonPatch>(request.Change.Content);
        currentJson = new(Id, Reference, patch.Apply(currentJson.Value, Hub.JsonSerializerOptions), request.ChangedBy, Hub.Version);
        dataChangedStream.OnNext(request);
        return ReduceManager.PatchFunction.Invoke(current.Value, currentJson.Value, patch, Hub.JsonSerializerOptions);
    }



    /// <summary>
    /// My current state deserialized as snapshot
    /// </summary>
    private ChangeItem<TStream> current;
    /// <summary>
    /// My current state deserialized as stream
    /// </summary>
    private readonly ReplaySubject<ChangeItem<TStream>> store = new(1);

    /// <summary>
    /// Current Json representation of stream state
    /// </summary>
    private ChangeItem<JsonElement> currentJson;

    private readonly ReplaySubject<JsonElement> currentJsonStream = new(1);
    /// <summary>
    /// My current state deserialized as stream
    /// </summary>
    private readonly Subject<DataChangedEvent> dataChangedStream = new();

    public IObservable<DataChangedEvent> DataChanged => dataChangedStream;

    public IObservable<DataChangedEvent> DataSynchronization =>
        currentJsonStream.Take(1)
            .Select(x => new DataChangedEvent(Id, Reference, Hub.Version, new(x.ToJsonString()), ChangeType.Full, null))
            .Concat(dataChangedStream);

    private readonly ImmutableArray<(
        Func<IMessageDelivery<WorkspaceMessage>, bool> Applies,
        Func<IMessageDelivery<WorkspaceMessage>, IMessageDelivery> Process
    )> messageHandlers = ImmutableArray<(
        Func<IMessageDelivery<WorkspaceMessage>, bool> Applies,
        Func<IMessageDelivery<WorkspaceMessage>, IMessageDelivery> Process
    )>.Empty;
    private readonly BackTransformation<TReference, TStream> backfeed;

    public IMessageHub Hub { get; }

    private readonly TaskCompletionSource<TStream> initialized = new();
    public Task<TStream> Initialized => initialized.Task;
    Task IChangeStream.Initialized => initialized.Task;



    object IChangeStream.Reference => Reference;

    private readonly ReduceManager<TStream> reduceManager;


    public ChangeStream(
        object id,
        IMessageHub hub,
        TReference reference,
        ReduceManager<TStream> reduceManager
    )
    {
        Id = id;
        Hub = hub;
        Reference = reference;
        this.reduceManager = reduceManager;
        backfeed = reduceManager.GetBackTransformation<TStream, TReference>();
    }


    public ReduceManager<TStream> ReduceManager => reduceManager;


    public IChangeStream<TReduced> Reduce<TReduced>(
        WorkspaceReference<TReduced> reference
    ) => (IChangeStream<TReduced>)ReduceMethod.MakeGenericMethod(typeof(TReduced), reference.GetType()).Invoke(this, [reference]);

    private static readonly MethodInfo ReduceMethod = ReflectionHelper.GetMethodGeneric<ChangeStream<TStream,TReference>>(x => x.Reduce<object, WorkspaceReference<object>>(null));
    private IChangeStream<TReduced> Reduce<TReduced, TReference2>(
        TReference2 reference
    )
        where TReference2 : WorkspaceReference<TReduced>
        => 
            reduceManager.ReduceStream<TReduced, TReference2>(this, reference);
    public IDisposable Subscribe(IObserver<ChangeItem<TStream>> observer)
    {
        return store.Subscribe(observer);
    }

    public readonly List<IDisposable> Disposables = new();

    public void Dispose()
    {
        foreach (var disposeAction in Disposables)
            disposeAction.Dispose();

        store.Dispose();
    }

    public ChangeItem<TStream> Current
    {
        get => current;
    }

    private void SetCurrent(ChangeItem<TStream> value)        
    {
        current = value;
        store.OnNext(value);
        if (!initialized.Task.IsCompleted)
            initialized.SetResult(value.Value);
    }


public void Initialize(TStream initial) =>
        Initialize(new ChangeItem<TStream>(Id, Reference, initial, null, Hub.Version));

    private void Initialize(ChangeItem<TStream> initial)
    {
        if (initial == null)
            throw new ArgumentNullException(nameof(initial));
        if (Current != null)
            throw new InvalidOperationException("Already initialized");

        SetCurrent(initial);
        MaintainPatchStream(initial);
    }

    public void OnCompleted()
    {
        store.OnCompleted();
    }

    public void OnError(Exception error)
    {
        store.OnError(error);
    }

    public void AddDisposable(IDisposable disposable) => Disposables.Add(disposable);

    IMessageDelivery IChangeStream.DeliverMessage(IMessageDelivery<WorkspaceMessage> delivery)
    {
        return messageHandlers
                .Where(x => x.Applies(delivery))
                .Select(x => x.Process)
                .FirstOrDefault()
                ?.Invoke(delivery) ?? delivery;
    }

    public void Update(Func<TStream, ChangeItem<TStream>> update)
    {
        if (!initialized.Task.IsCompleted)
            Initialize(update(default));
        else
        {
            SetCurrent(update(Current.Value));
            MaintainPatchStream(current);
        }
    }

    public DataChangeResponse RequestChange(DataChangedEvent request, object changedBy)
    {
        return RequestChange(state => Change(request, changedBy));
    }

    public DataChangeResponse RequestChange(Func<TStream, ChangeItem<TStream>> update)
    {
        if (backfeed != null)
            return Hub.GetWorkspace().RequestChange(state => backfeed(state, Reference, update(Current.Value)));
        SetCurrent(update(Current.Value));
        return new DataChangeResponse(Hub.Version, DataChangeStatus.Committed, null);
    }

    private void MaintainPatchStream(ChangeItem<TStream> change)
    {
        var serialized = JsonSerializer.SerializeToElement(change.Value, Hub.JsonSerializerOptions);
        if(currentJson?.Value != null)
        {
            var dataChanged = GetPatch(change, serialized);
            if (dataChanged != null)
                dataChangedStream.OnNext(dataChanged);
        } 
        currentJson = change.SetValue(serialized);
        currentJsonStream.OnNext(serialized);
    }

    private DataChangedEvent GetPatch(ChangeItem<TStream> change, JsonElement serialized)
    {
        var jsonPatch = currentJson.Value.CreatePatch(serialized);
        if(jsonPatch.Operations.Count == 0)
            return null;
        return new DataChangedEvent(Id, Reference, Hub.Version,  new(JsonSerializer.Serialize(jsonPatch, Hub.JsonSerializerOptions)), ChangeType.Patch, change.ChangedBy);
    }

    private ChangeItem<TStream> Change(DataChangedEvent request, object changedBy)
    {
        if(request.ChangeType == ChangeType.Full)
            throw new InvalidOperationException("Cannot apply full change to stream");
        var patch = JsonSerializer.Deserialize<JsonPatch>(request.Change.Content);
        currentJson = new(Id, Reference, patch.Apply(currentJson.Value), request.ChangedBy, Hub.Version);
        return new ChangeItem<TStream>(
            Id,
            Reference,
            reduceManager.PatchFunction.Invoke(current.Value, currentJson.Value, patch, Hub.JsonSerializerOptions),
            changedBy,
            Hub.Version
        );
    }

    public void OnNext(ChangeItem<TStream> value)
    {
        SetCurrent(value);
        MaintainPatchStream(value);
    }

}
