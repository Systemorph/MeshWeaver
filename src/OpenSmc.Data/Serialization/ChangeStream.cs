using System.Collections.Immutable;
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

    protected JsonNode LastSynchronized { get; set; }

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

        updatedInstances
            .CombineLatest(
                updates.StartWith(x => x),
                (value, update) =>
                    new ChangeItem<TStream>(Id, Reference, update(value.Value), Id, Hub.Version)
            )
            .Subscribe(store);

        this.reduceManager = reduceManager;

        RegisterMessageHandler<DataChangedEvent>(delivery =>
        {
            Synchronize(delivery.Message);
            return delivery.Processed();
        });
        RegisterMessageHandler<PatchChangeRequest>(delivery =>
        {
            Change(delivery.Message, delivery.Sender);
            return delivery.Processed();
        });
    }

    private void Change(PatchChangeRequest request, object changedBy)
    {
        if (LastSynchronized == null)
            throw new ArgumentException("Cannot patch workspace which has not been initialized.");

        var patch = request.Change;
        var newState = patch.Apply(LastSynchronized);
        LastSynchronized = newState.Result;

        store.OnNext(
            new ChangeItem<TStream>(
                Id,
                Reference,
                newState.Result.Deserialize<TStream>(Hub.DeserializationOptions),
                changedBy,
                Hub.Version
            )
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
        IObservable<DataChangedEvent> stream = outgoingChanges;

        if (LastSynchronized != null)
            observer.OnNext(
                new DataChangedEvent(
                    Id,
                    Reference,
                    Hub.Version,
                    LastSynchronized,
                    ChangeType.Full,
                    Id
                )
            );
        return stream.Subscribe(observer);
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
        var dataChanged = GetDataChanged(value);
        updatedInstances.OnNext(value);
        if (outgoingChanges.HasObservers)
            outgoingChanges.OnNext(GetDataChanged(value));
    }

    private DataChangedEvent GetDataChanged(ChangeItem<TStream> change)
    {
        var node = JsonSerializer.SerializeToNode(
            change.Value,
            typeof(TStream),
            Hub.JsonSerializerOptions
        );

        var dataChanged = LastSynchronized == null ? GetFullDataChange(change) : GetPatch(node);
        LastSynchronized = node;
        return dataChanged;
    }

    public void Synchronize(DataChangedEvent request)
    {
        updatedInstances.OnNext(
            new ChangeItem<TStream>(
                request.Address,
                (WorkspaceReference)request.Reference,
                ParseDataChanged(request),
                request.ChangedBy,
                request.Version
            )
        );
    }

    private TStream ParseDataChanged(DataChangedEvent request)
    {
        var newState = request.ChangeType switch
        {
            ChangeType.Patch
                => (
                    LastSynchronized = ((JsonPatch)request.Change).Apply(LastSynchronized).Result
                ).Deserialize<TStream>(Hub.JsonSerializerOptions),
            ChangeType.Full => GetFullState(request),
            _ => throw new ArgumentOutOfRangeException()
        };
        return newState;
    }

    private TStream GetFullState(DataChangedEvent request)
    {
        LastSynchronized = JsonSerializer.SerializeToNode(
            request.Change,
            request.Change.GetType(),
            Hub.JsonSerializerOptions
        );
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
        outgoingChanges.OnNext(GetFullDataChange(start));
        return this;
    }

    private DataChangedEvent GetPatch(JsonNode node)
    {
        var jsonPatch = LastSynchronized.CreatePatch(node);
        if (!jsonPatch.Operations.Any())
            return null;
        return new DataChangedEvent(
            Id,
            Reference,
            Hub.Version,
            jsonPatch,
            ChangeType.Patch,
            Hub.Address
        );
    }

    private DataChangedEvent GetFullDataChange(ChangeItem<TStream> value)
    {
        return new DataChangedEvent(
            Id,
            Reference,
            Hub.Version,
            value.Value,
            ChangeType.Full,
            value.ChangedBy
        );
    }

    public void OnCompleted()
    {
        store.OnCompleted();
    }

    public void OnError(Exception error)
    {
        store.OnError(error);
    }

    public void OnNext(DataChangedEvent value) => Synchronize(value);

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
