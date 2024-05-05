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
    : IObservable<DataChangedEvent>,
        IObservable<PatchChangeRequest>,
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

    public object Id { get; init; }
    public WorkspaceReference<TStream> Reference { get; init; }

    WorkspaceReference IChangeStream.Reference => Reference;

    private readonly IReduceManager<TStream> reduceManager;
    private JsonNode lastSynchronized;
    private IObservable<DataChangedEvent> dataChangedStream;
    private IObservable<PatchChangeRequest> patchRequestStream;

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

        this.reduceManager = reduceManager;

        RegisterMessageHandler<DataChangedEvent>(delivery =>
        {
            Synchronize(delivery.Message);
            return delivery.Processed();
        });
        RegisterMessageHandler<PatchChangeRequest>(delivery =>
        {
            updatedInstances.OnNext(Change(delivery));
            return delivery.Processed();
        });

        var baseChangeStream = store
            .Select(r => GetDataChanged(r))
            .Where(x => x != null && x.Change != null)
            .GroupBy(x => Id.Equals(x.ChangedBy));

        dataChangedStream = baseChangeStream.Where(x => x.Key).SelectMany(x => x);
        patchRequestStream = baseChangeStream
            .Where(x => !x.Key)
            .SelectMany(x =>
                x.Select(y => new PatchChangeRequest(y.Address, y.Reference, (JsonPatch)y.Change))
            );
    }

    private ChangeItem<TStream> Change(IMessageDelivery<PatchChangeRequest> delivery)
    {
        //TODO Roland Bürgi 2024-05-04: Check if patch is permitted or not
        var request = delivery.Message;

        Hub.Post(
            new DataChangeResponse(Hub.Version, DataChangeStatus.Committed),
            o => o.ResponseFor(delivery)
        );
        return new ChangeItem<TStream>(
            Id,
            Reference,
            ApplyPatch(delivery.Message.Change).Deserialize<TStream>(Hub.JsonSerializerOptions),
            delivery.Sender,
            Hub.Version
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
        if (lastSynchronized == null)
            return dataChangedStream.Subscribe(observer);
        return Observable
            .Return(
                new DataChangedEvent(
                    Id,
                    Reference,
                    Hub.Version,
                    lastSynchronized,
                    ChangeType.Full,
                    null
                )
            )
            .Merge(dataChangedStream.Where(x => x.ChangeType == ChangeType.Patch))
            .Subscribe(observer);
    }

    public IDisposable Subscribe(IObserver<PatchChangeRequest> observer)
    {
        return patchRequestStream.Subscribe(observer);
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
        var fullChange = JsonSerializer.SerializeToNode(change.Value, Hub.JsonSerializerOptions);

        var dataChanged = new DataChangedEvent(
            Id,
            Reference,
            change.Version,
            lastSynchronized == null ? fullChange : GetPatch(fullChange),
            lastSynchronized == null ? ChangeType.Full : ChangeType.Patch,
            change.ChangedBy
        );

        lastSynchronized = fullChange;

        return dataChanged;
    }

    public void Synchronize(DataChangedEvent request)
    {
        updatedInstances.OnNext(ParseDataChanged(request));
    }

    private ChangeItem<TStream> ParseDataChanged(DataChangedEvent request)
    {
        var newState = request.ChangeType switch
        {
            ChangeType.Patch
                => ApplyPatch((JsonPatch)request.Change)
                    .Deserialize<TStream>(Hub.JsonSerializerOptions),
            ChangeType.Full => GetFullState(request),
            _ => throw new ArgumentOutOfRangeException()
        };
        return new ChangeItem<TStream>(Id, Reference, newState, request.ChangedBy, Hub.Version);
    }

    private ChangeItem<TStream> ApplyPatchRequest(PatchChangeRequest request, object changedBy)
    {
        var newState = ApplyPatch((JsonPatch)request.Change)
            .Deserialize<TStream>(Hub.JsonSerializerOptions);
        ;
        return new ChangeItem<TStream>(Id, Reference, newState, changedBy, Hub.Version);
    }

    private JsonNode ApplyPatch(JsonPatch patch)
    {
        var applied = patch.Apply(lastSynchronized);
        if (applied.Error != null)
            throw new InvalidOperationException(applied.Error);
        ;
        return lastSynchronized = applied.Result;
    }

    private TStream GetFullState(DataChangedEvent request)
    {
        lastSynchronized =
            request.Change as JsonNode
            ?? JsonSerializer.SerializeToNode(request.Change, Hub.JsonSerializerOptions);
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

        return this;
    }

    private JsonPatch GetPatch(JsonNode fullChange)
    {
        var jsonPatch = lastSynchronized.CreatePatch(fullChange);
        if (!jsonPatch.Operations.Any())
            return null;
        return jsonPatch;
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
        if (changeItem.ChangedBy != null)
            updatedInstances.OnNext(changeItem);
    }

    public void Update(Func<TStream, TStream> update)
    {
        updates.OnNext(update);
    }
}
