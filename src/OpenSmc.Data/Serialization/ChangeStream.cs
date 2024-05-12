using System.Collections.Immutable;
using System.Data;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Patch;
using OpenSmc.Activities;
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
    Task Initialized { get; }
}

public interface IChangeStream<TStream, TOriginalStream>
    : IChangeStream,
        IObservable<ChangeItem<TStream>>
{
    IObservable<ChangeItem<TOriginalStream>> Synchronization { get; }
    void Synchronize(Func<TStream, ChangeItem<TStream>> update);
    IObservable<ChangeItem<TReduced>> Reduce<TReduced>(WorkspaceReference<TReduced> reference);
    void Initialize(TStream value);
}

public record ChangeStream<TStream, TOriginalStream>
    : IChangeStream<TStream, TOriginalStream>,
        IObservable<ChangeItem<TStream>>
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

    private readonly TaskCompletionSource initialized = new();
    public Task Initialized => initialized.Task;

    /// <summary>
    /// Pending change requests
    /// </summary>
    private readonly Subject<Func<TStream, ChangeItem<TStream>>> incomingUpdates = new();

    public object Id { get; init; }
    public WorkspaceReference<TStream> Reference { get; init; }

    WorkspaceReference IChangeStream.Reference => Reference;

    private readonly ReduceManager<TStream> reduceManager;
    private readonly Func<TStream, TOriginalStream> backfeed;
    private IObservable<DataChangedEvent> dataChangedStream;
    private IObservable<PatchChangeRequest> patchRequestStream;

    /// <summary>
    /// My current state
    /// </summary>
    private readonly ReplaySubject<ChangeItem<TStream>> store = new(1);

    public ChangeStream(
        object Id,
        WorkspaceReference<TStream> Reference,
        IMessageHub Hub,
        ReduceManager<TStream> reduceManager,
        Func<TStream, TOriginalStream> backfeed
    )
        : this(Id, Reference, Hub, reduceManager, backfeed, Observable.Empty<ChangeItem<TStream>>())
    { }

    public ChangeStream(
        object Id,
        WorkspaceReference<TStream> Reference,
        IMessageHub Hub,
        ReduceManager<TStream> reduceManager,
        Func<TStream, TOriginalStream> backfeed,
        IObservable<ChangeItem<TStream>> store
    )
    {
        this.Id = Id;
        this.Reference = Reference;
        this.Hub = Hub;
        Disposables.Add(store.Subscribe(this.store));
        this.reduceManager = reduceManager;
        this.backfeed = backfeed;
        RegisterMessageHandler<DataChangedEvent>(delivery =>
        {
            Synchronize(delivery.Message);
            return delivery.Processed();
        });
        RegisterMessageHandler<PatchChangeRequest>(delivery =>
        {
            incomingUpdates.OnNext(state => Change(delivery, state));
            return delivery.Processed();
        });

        dataChangedStream = this
            .store.Skip(1)
            .Where(x => !Hub.Address.Equals(x.ChangedBy) || !Reference.Equals(x.Reference))
            .Select(r => GetDataChanged(r))
            .Where(x => x?.Change != null);

        var myOutgoingUpdates = this.store.Where(x =>
            Id.Equals(x.ChangedBy) && Reference.Equals(x.Reference)
        );

        var backTransformation =
            backfeed ?? reduceManager?.GetBackTransformation<TOriginalStream>();
        Synchronization =
            backTransformation == null
                ? Observable.Empty<ChangeItem<TOriginalStream>>()
                : myOutgoingUpdates.Select(x => x.SetValue(backTransformation(x.Value)));

        patchRequestStream = myOutgoingUpdates
            .Select(r => GetDataChanged(r))
            .Where(x => x?.Change != null)
            .Select(x => new PatchChangeRequest(x.Address, x.Reference, (JsonPatch)x.Change));
    }

    private ChangeItem<TStream> Change(IMessageDelivery<PatchChangeRequest> delivery, TStream state)
    {
        var log = new ActivityLog(ActivityCategory.DataUpdate);
        var patched = delivery.Message.Change.Apply(state, Hub.JsonSerializerOptions);
        if (patched == null)
        {
            new ChangeItem<TStream>(Id, Reference, state, delivery.Sender, Hub.Version)
            {
                Log = log
            };
        }

        return (
            new ChangeItem<TStream>(Id, Reference, patched, delivery.Sender, Hub.Version)
            {
                Log = log.Finish()
            }
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

    private DataChangedEvent GetDataChanged(ChangeItem<TStream> change)
    {
        var fullChange = change.Value;

        var dataChanged = new DataChangedEvent(
            Id,
            Reference,
            change.Version,
            Current == null ? fullChange : GetPatch(fullChange),
            Current == null ? ChangeType.Full : ChangeType.Patch,
            change.ChangedBy
        );

        Current = fullChange;

        return dataChanged;
    }

    public void Synchronize(DataChangedEvent request)
    {
        if (Current == null)
            Initialize(GetFullState(request));
        else
            incomingUpdates.OnNext(s => Merge(s, ParseDataChangedFromLastSynchronized(request)));
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
        var applied = patch.Apply(Current, Hub.JsonSerializerOptions);
        return Current = applied;
    }

    private TStream GetFullState(DataChangedEvent request)
    {
        return Current = request.Change is TStream s
            ? s
            : (request.Change as JsonNode).Deserialize<TStream>(Hub.JsonSerializerOptions)
                ?? throw new InvalidOperationException();
    }

    public TStream Current { get; private set; }

    public IObservable<ChangeItem<TOriginalStream>> Synchronization { get; }

    public void Initialize(TStream initial) =>
        Initialize(new ChangeItem<TStream>(Id, Reference, initial, Id, Hub.Version));

    private void Initialize(ChangeItem<TStream> initial)
    {
        if (initial == null)
            throw new ArgumentNullException(nameof(initial));
        if (Current != null)
            throw new InvalidOperationException("Already initialized");

        Current = initial.Value;
        Disposables.Add(
            incomingUpdates
                .StartWith(_ => initial)
                .Select(updateFunc => updateFunc(Current))
                .Subscribe(store)
        );

        initialized.SetResult();
    }

    private JsonPatch GetPatch(TStream fullChange)
    {
        var jsonPatch = Current.CreatePatch(fullChange, Hub.JsonSerializerOptions);
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

    public void Synchronize(Func<TStream, ChangeItem<TStream>> update)
    {
        if (Current == null)
            Initialize(update(default));
        else
            incomingUpdates.OnNext(update);
    }

    private ChangeItem<TStream> Merge(TStream _, ChangeItem<TStream> changeItem)
    {
        //TODO Roland Bürgi 2024-05-06: Apply some merge logic
        return changeItem;
    }
}
