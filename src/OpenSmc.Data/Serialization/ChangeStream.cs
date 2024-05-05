using System.Collections.Immutable;
using System.Data;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Patch;
using OpenSmc.Messaging;

namespace OpenSmc.Data.Serialization;

public interface IChangeStream
    : IObserver<DataChangedEvent>,
        IObservable<DataChangedEvent>,
        IDisposable
{
    object Id { get; }
    WorkspaceReference Reference { get; }

    IMessageDelivery DeliverMessage(IMessageDelivery<IWorkspaceMessage> delivery);
    void AddDisposable(IDisposable disposable);

    void Synchronize(DataChangedEvent request);
}

public record ChangeStream<TStream> : IChangeStream, IObservable<ChangeItem<TStream>>
{
    private IDisposable updateSubscription;

    private ImmutableArray<(
        Func<IMessageDelivery<IWorkspaceMessage>, bool> Applies,
        Func<IMessageDelivery<IWorkspaceMessage>, IMessageDelivery> Process
    )> messageHandlers = ImmutableArray<(
        Func<IMessageDelivery<IWorkspaceMessage>, bool> Applies,
        Func<IMessageDelivery<IWorkspaceMessage>, IMessageDelivery> Process
    )>.Empty;

    private IMessageHub Hub { get; }

    /// <summary>
    /// My current state
    /// </summary>
    private readonly ReplaySubject<ChangeItem<TStream>> store = new(1);

    /// <summary>
    /// Pending change requests
    /// </summary>
    private readonly Subject<Func<TStream, TStream>> updates = new();
    private readonly Subject<ChangeItem<TStream>> updatedInstances = new();
    private readonly Subject<DataChangedEvent> outgoingChanges = new();
    private DataChangedEvent lastSynchronizedBacking;
    private DataChangedEvent LastSynchronized
    {
        get { return lastSynchronizedBacking; }
        set
        {
            lastSynchronizedBacking = value;
            initialDataChanged.OnNext(value);
        }
    }
    private readonly ReplaySubject<DataChangedEvent> initialDataChanged = new(1);
    private readonly Subject<DataChangedEvent> incomingChanges = new();
    private readonly Subject<IMessageDelivery<PatchChangeRequest>> incomingPatches = new();
    private readonly Subject<JsonNode> incomingJson = new();

    public object Id { get; init; }
    public WorkspaceReference<TStream> Reference { get; init; }

    WorkspaceReference IChangeStream.Reference => Reference;

    private readonly IReduceManager<TStream> reduceManager;

    public ChangeStream(
        object Id,
        WorkspaceReference<TStream> Reference,
        IMessageHub Hub,
        IReduceManager<TStream> reduceManager
    )
    {
        this.Id = Id;
        this.Reference = Reference;
        this.Hub = Hub;

        // updating instances
        updatedInstances
            .CombineLatest(
                updates.StartWith(x => x),
                (value, update) => value.SetValue(update(value.Value))
            )
            .Subscribe(store);

        // incoming changes
        Disposables.Add(incomingPatches.Select(Change).Subscribe(incomingChanges));
        Disposables.Add(incomingChanges.Select(x => ParseDataChanged(x)).Subscribe(store));
        Disposables.Add(incomingChanges.Subscribe(outgoingChanges));
        Disposables.Add(updatedInstances.Select(GetDataChanged).Subscribe(outgoingChanges));

        this.reduceManager = reduceManager;

        RegisterMessageHandler<DataChangedEvent>(delivery =>
        {
            Synchronize(delivery.Message);
            return delivery.Processed();
        });
        RegisterMessageHandler<PatchChangeRequest>(delivery =>
        {
            incomingPatches.OnNext(delivery);
            return delivery.Processed();
        });
    }

    private DataChangedEvent Change(IMessageDelivery<PatchChangeRequest> delivery)
    {
        //TODO Roland Bürgi 2024-05-04: Check if patch is permitted or not
        var request = delivery.Message;

        Hub.Post(
            new DataChangeResponse(Hub.Version, DataChangeStatus.Committed),
            o => o.ResponseFor(delivery)
        );
        return new DataChangedEvent(
            Id,
            Reference,
            Hub.Version,
            request.Change,
            ChangeType.Patch,
            delivery.Sender
        );
    }

    public void RegisterMessageHandler<TMessage>(
        Func<IMessageDelivery<TMessage>, IMessageDelivery> process
    )
        where TMessage : IWorkspaceMessage
    {
        RegisterMessageHandler<TMessage>(process, (Func<TMessage, bool>)(_ => true));
    }

    public void RegisterMessageHandler<TMessage>(
        Func<IMessageDelivery<TMessage>, IMessageDelivery> process,
        Func<TMessage, bool> applies
    )
        where TMessage : IWorkspaceMessage
    {
        messageHandlers = messageHandlers.Insert(
            0,
            (
                x => x is IMessageDelivery<TMessage> delivery && applies(delivery.Message),
                x => process((IMessageDelivery<TMessage>)x)
            )
        );
    }

    public IObservable<ChangeItem<TReduced>> Reduce<TReduced>(
        WorkspaceReference<TReduced> reference
    ) => reduceManager.ReduceStream(store, reference);

    public IDisposable Subscribe(IObserver<ChangeItem<TStream>> observer)
    {
        return store.Subscribe(observer);
    }

    public IDisposable Subscribe(IObserver<DataChangedEvent> observer)
    {
        return initialDataChanged.Take(1).Merge(outgoingChanges).Subscribe(observer);
    }

    public readonly List<IDisposable> Disposables = new();

    public void Dispose()
    {
        foreach (var disposeAction in Disposables)
            disposeAction.Dispose();

        store.Dispose();
        updateSubscription?.Dispose();
    }

    public void Synchronize(ChangeItem<TStream> value)
    {
        updatedInstances.OnNext(value);
    }

    private DataChangedEvent GetDataChanged(ChangeItem<TStream> change)
    {
        var fullChange = GetFullDataChange(change);

        var dataChanged = LastSynchronized == null ? fullChange : GetPatch(fullChange);

        return dataChanged;
    }

    public void Synchronize(DataChangedEvent request)
    {
        incomingChanges.OnNext(request);
    }

    private ChangeItem<TStream> ParseDataChanged(DataChangedEvent request)
    {
        var newState = request.ChangeType switch
        {
            ChangeType.Patch
                => (ApplyPatch(request)).Deserialize<TStream>(Hub.JsonSerializerOptions),
            ChangeType.Full => GetFullState(LastSynchronized = request),
            _ => throw new ArgumentOutOfRangeException()
        };
        return new ChangeItem<TStream>(Id, Reference, newState, request.ChangedBy, Hub.Version);
    }

    private JsonNode ApplyPatch(DataChangedEvent request)
    {
        var ret = ((JsonPatch)request.Change).Apply((JsonNode)LastSynchronized.Change).Result;
        LastSynchronized = request with { Change = ret, ChangeType = ChangeType.Full };
        return ret;
    }

    private TStream GetFullState(DataChangedEvent request)
    {
        LastSynchronized = request;

        return request.Change is JsonNode node
            ? node.Deserialize<TStream>(Hub.JsonSerializerOptions)
            : (TStream)request.Change;
    }

    public ChangeStream<TStream> Initialize(TStream initial)
    {
        if (initial == null)
            throw new ArgumentNullException(nameof(initial));
        var start = new ChangeItem<TStream>(Id, Reference, initial, Id, Hub.Version);
        updatedInstances.OnNext(start);
        LastSynchronized = new DataChangedEvent(
            Id,
            Reference,
            Hub.Version,
            initial,
            ChangeType.Full,
            Id
        );
        return this;
    }

    private DataChangedEvent GetPatch(DataChangedEvent fullChange)
    {
        var jsonPatch = LastSynchronized.CreatePatch(fullChange.Change);
        if (!jsonPatch.Operations.Any())
            return null;
        return fullChange with { Change = jsonPatch, ChangeType = ChangeType.Patch };
    }

    private DataChangedEvent GetFullDataChange(ChangeItem<TStream> value)
    {
        return new DataChangedEvent(Id, Reference, Hub.Version, value.Value, ChangeType.Full, Id);
    }

    public void OnCompleted()
    {
        store.OnCompleted();
    }

    public void OnError(Exception error)
    {
        store.OnError(error);
    }

    public void OnNext(DataChangedEvent value) => incomingChanges.OnNext(value);

    public void AddDisposable(IDisposable disposable) => Disposables.Add(disposable);

    IMessageDelivery IChangeStream.DeliverMessage(IMessageDelivery<IWorkspaceMessage> delivery)
    {
        return messageHandlers
            .Where(x => x.Applies(delivery))
            .Select(x => x.Process)
            .FirstOrDefault()
            ?.Invoke(delivery);
    }

    public void Update(ChangeItem<TStream> changeItem)
    {
        updatedInstances.OnNext(changeItem);
    }

    public void Update(Func<TStream, TStream> update)
    {
        updates.OnNext(update);
    }
}
