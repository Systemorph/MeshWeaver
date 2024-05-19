using System.Collections.Immutable;
using System.Data;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using OpenSmc.Activities;
using OpenSmc.Messaging;

namespace OpenSmc.Data.Serialization;

public interface IChangeStream : IDisposable
{
    object Id { get; }
    WorkspaceReference Reference { get; }

    internal IMessageDelivery DeliverMessage(IMessageDelivery<WorkspaceMessage> delivery);
    void AddDisposable(IDisposable disposable);

    Task Initialized { get; }

    IMessageHub Hub { get; }
    IWorkspace Workspace { get; }
    void RegisterMessageHandler<TMessage>(
        Func<IMessageDelivery<TMessage>, IMessageDelivery> process
    )
        where TMessage : WorkspaceMessage => RegisterMessageHandler<TMessage>(process, _ => true);

    void RegisterMessageHandler<TMessage>(
        Func<IMessageDelivery<TMessage>, IMessageDelivery> process,
        Func<TMessage, bool> applies
    )
        where TMessage : WorkspaceMessage;

    public void Post(WorkspaceMessage message) =>
        Hub.Post(message with { Address = Id, Reference = Reference });
}

public interface IChangeStream<TStream>
    : IChangeStream,
        IObservable<ChangeItem<TStream>>,
        IObserver<ChangeItem<TStream>>
{
    void Update(Func<TStream, ChangeItem<TStream>> update);
    void Initialize(TStream value);
    IObservable<ChangeItem<TReduced>> Reduce<TReduced>(WorkspaceReference<TReduced> reference);

    new Task<TStream> Initialized { get; }

    DataChangeResponse RequestChange(Func<TStream, ChangeItem<TStream>> update);
}

public interface IChangeStream<TStream, TReference> : IChangeStream<TStream>
    where TReference : WorkspaceReference<TStream>
{
    ChangeItem<TStream> Current { get; }
    new TReference Reference { get; }
}

public record ChangeStream<TStream, TReference>
    : IChangeStream<TStream, TReference>,
        IObservable<ChangeItem<TStream>>
    where TReference : WorkspaceReference<TStream>
{
    public TReference Reference { get; init; }

    private ImmutableArray<(
        Func<IMessageDelivery<WorkspaceMessage>, bool> Applies,
        Func<IMessageDelivery<WorkspaceMessage>, IMessageDelivery> Process
    )> messageHandlers = ImmutableArray<(
        Func<IMessageDelivery<WorkspaceMessage>, bool> Applies,
        Func<IMessageDelivery<WorkspaceMessage>, IMessageDelivery> Process
    )>.Empty;
    private readonly Func<
        WorkspaceState,
        TReference,
        ChangeItem<TStream>,
        ChangeItem<WorkspaceState>
    > backfeed;

    public IMessageHub Hub => Workspace.Hub;
    public IWorkspace Workspace { get; }

    private readonly TaskCompletionSource<TStream> initialized = new();
    public Task<TStream> Initialized => initialized.Task;
    Task IChangeStream.Initialized => initialized.Task;

    public object Id { get; init; }

    WorkspaceReference IChangeStream.Reference => Reference;

    private readonly ReduceManager<TStream> reduceManager;
    private ChangeItem<TStream> current;

    /// <summary>
    /// My current state
    /// </summary>
    private readonly ReplaySubject<ChangeItem<TStream>> store = new(1);

    public ChangeStream(
        object id,
        TReference reference,
        IWorkspace workspace,
        ReduceManager<TStream> reduceManager
    )
    {
        Id = id;
        Reference = reference;
        Workspace = workspace;
        this.reduceManager = reduceManager;
        backfeed = reduceManager.ReduceTo<TStream>().GetBackfeed<TReference>();
    }

    public void RegisterMessageHandler<TMessage>(
        Func<IMessageDelivery<TMessage>, IMessageDelivery> process,
        Func<TMessage, bool> applies
    )
        where TMessage : WorkspaceMessage
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
        private set
        {
            current = value;
            store.OnNext(value);
        }
    }

    public void Initialize(TStream initial) =>
        Initialize(new ChangeItem<TStream>(Id, Reference, initial, null, Hub.Version));

    private void Initialize(ChangeItem<TStream> initial)
    {
        if (initial == null)
            throw new ArgumentNullException(nameof(initial));
        if (Current != null)
            throw new InvalidOperationException("Already initialized");

        Current = initial;
        initialized.SetResult(initial.Value);
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
            ?.Invoke(delivery);
    }

    public void Update(Func<TStream, ChangeItem<TStream>> update)
    {
        if (Current == null)
            Initialize(update(default));
        Current = update(Current.Value);
    }

    private ChangeItem<TStream> Merge(TStream _, ChangeItem<TStream> changeItem)
    {
        //TODO Roland Bürgi 2024-05-06: Apply some merge logic
        return changeItem;
    }

    public DataChangeResponse RequestChange(Func<TStream, ChangeItem<TStream>> update)
    {
        if (backfeed == null)
            return new DataChangeResponse(
                0,
                DataChangeStatus.Failed,
                new ActivityLog(ActivityCategory.DataUpdate).Fail(
                    $"Was not able to backtransform the change item of type {typeof(TStream).Name}"
                )
            );

        return Workspace.RequestChange(state => backfeed(state, Reference, update(Current.Value)));
    }

    public void OnNext(ChangeItem<TStream> value)
    {
        Current = value;
    }
}
