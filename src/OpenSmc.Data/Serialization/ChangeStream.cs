using System.Collections.Immutable;
using System.Data;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reflection.Metadata.Ecma335;
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
    private readonly Subject<Func<TStream, ChangeItem<TStream>>> updates = new();
    private readonly Subject<TStream> updatedInstances = new();

    public object Id { get; init; }
    public WorkspaceReference<TStream> Reference { get; init; }

    WorkspaceReference IChangeStream.Reference => Reference;

    private readonly IReduceManager<TStream> reduceManager;
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
                updates.StartWith(x => new ChangeItem<TStream>(
                    Id,
                    Reference,
                    x,
                    null,
                    Hub.Version
                )),
                (value, update) => update(value)
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
            updates.OnNext(state => Change(delivery, state));
            return delivery.Processed();
        });

        dataChangedStream = store
            .Skip(1)
            .Where(x => Id.Equals(x.ChangedBy))
            .Select(r => GetDataChanged(r))
            .Where(x => x?.Change != null);
        patchRequestStream = store
            .Skip(1)
            .Where(x => !Id.Equals(x.ChangedBy))
            .Select(r => GetDataChanged(r))
            .Where(x => x?.Change != null)
            .Select(x => new PatchChangeRequest(x.Address, x.Reference, (JsonPatch)x.Change));
    }

    private ChangeItem<TStream> Change(IMessageDelivery<PatchChangeRequest> delivery, TStream state)
    {
        var patched = delivery.Message.Change.Apply(state);
        if (patched == null)
            throw new InvalidOperationException();

        return new ChangeItem<TStream>(Id, Reference, patched, delivery.Sender, Hub.Version);
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
        return store
            .Take(1)
            .Select(GetFullDataChange)
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

    public void Synchronize(ChangeItem<TStream> changeItem)
    {
        updates.OnNext(s => Merge(s, changeItem));
    }

    private DataChangedEvent GetDataChanged(ChangeItem<TStream> change)
    {
        var fullChange = change.Value;

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
        if (lastSynchronized == null)
            Initialize(GetFullState(request));
        else
            updates.OnNext(s => Merge(s, ParseDataChangedFromLastSynchronized(request)));
    }

    private ChangeItem<TStream> ParseDataChangedFromLastSynchronized(DataChangedEvent request)
    {
        var newState = request.ChangeType switch
        {
            ChangeType.Patch => ApplyPatch((JsonPatch)request.Change),
            ChangeType.Full => GetFullState(request),
            _ => throw new ArgumentOutOfRangeException()
        };
        return new ChangeItem<TStream>(Id, Reference, newState, request.ChangedBy, Hub.Version);
    }

    private ChangeItem<TStream> ApplyPatchRequest(PatchChangeRequest request, object changedBy)
    {
        var newState = ApplyPatch((JsonPatch)request.Change);
        ;
        return new ChangeItem<TStream>(Id, Reference, newState, changedBy, Hub.Version);
    }

    private TStream ApplyPatch(JsonPatch patch)
    {
        var applied = patch.Apply(lastSynchronized, Hub.JsonSerializerOptions);
        return lastSynchronized = applied;
    }

    private TStream GetFullState(DataChangedEvent request)
    {
        return lastSynchronized = request.Change is TStream s
            ? s
            : (request.Change as JsonNode).Deserialize<TStream>(Hub.JsonSerializerOptions)
                ?? throw new InvalidOperationException();
    }

    private TStream lastSynchronized;

    public ChangeStream<TStream> Initialize(TStream initial)
    {
        if (initial == null)
            throw new ArgumentNullException(nameof(initial));
        lastSynchronized = initial;
        updatedInstances.OnNext(initial);

        return this;
    }

    private JsonPatch GetPatch(TStream fullChange)
    {
        var jsonPatch = lastSynchronized.CreatePatch(fullChange, Hub.JsonSerializerOptions);
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

    public void Update(Func<TStream, ChangeItem<TStream>> update)
    {
        updates.OnNext(update);
    }

    private ChangeItem<TStream> Merge(TStream _, ChangeItem<TStream> changeItem)
    {
        //TODO Roland Bürgi 2024-05-06: Apply some merge logic
        return changeItem;
    }
}
