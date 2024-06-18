using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reflection;
using OpenSmc.Messaging;
using OpenSmc.Reflection;

namespace OpenSmc.Data.Serialization;

public record ChangeStream<TStream, TReference> : IChangeStream<TStream, TReference>
    where TReference : WorkspaceReference
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

    /// <summary>
    /// My current state deserialized as snapshot
    /// </summary>
    private ChangeItem<TStream> current;

    /// <summary>
    /// My current state deserialized as stream
    /// </summary>
    protected readonly ReplaySubject<ChangeItem<TStream>> Store = new(1);

    public ReduceManager<TStream> ReduceManager { get; }

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
        ReduceManager = reduceManager;
        backfeed = reduceManager.GetBackTransformation<TStream, TReference>();
    }

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

    public IChangeStream<TReduced> Reduce<TReduced>(WorkspaceReference<TReduced> reference) =>
        (IChangeStream<TReduced>)
            ReduceMethod
                .MakeGenericMethod(typeof(TReduced), reference.GetType())
                .Invoke(this, [reference]);

    private static readonly MethodInfo ReduceMethod = ReflectionHelper.GetMethodGeneric<
        ChangeStream<TStream, TReference>
    >(x => x.Reduce<object, WorkspaceReference<object>>(null));

    private IChangeStream<TReduced> Reduce<TReduced, TReference2>(TReference2 reference)
        where TReference2 : WorkspaceReference<TReduced> =>
        ReduceManager.ReduceStream<TReduced, TReference2>(this, reference);

    public virtual IDisposable Subscribe(IObserver<ChangeItem<TStream>> observer)
    {
        return Store.Subscribe(observer);
    }

    public readonly List<IDisposable> Disposables = new();

    public void Dispose()
    {
        foreach (var disposeAction in Disposables)
            disposeAction.Dispose();

        Store.Dispose();
    }

    protected ChangeItem<TStream> Current
    {
        get => current;
        set => SetCurrent(value);
    }

    private void SetCurrent(ChangeItem<TStream> value)
    {
        current = value;
        Store.OnNext(value);
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
    }

    public void OnCompleted()
    {
        Store.OnCompleted();
    }

    public void OnError(Exception error)
    {
        Store.OnError(error);
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
        }
    }

    public void OnNext(ChangeItem<TStream> value)
    {
        SetCurrent(value);
    }

    public DataChangeResponse RequestChange(Func<TStream, ChangeItem<TStream>> update)
    {
        SetCurrent(update(Current.Value));
        return Backfeed(update);
    }

    protected DataChangeResponse Backfeed(Func<TStream, ChangeItem<TStream>> update)
    {
        if (backfeed != null)
            return Hub.GetWorkspace()
                .RequestChange(state => backfeed(state, Reference, update(Current.Value)));
        return new DataChangeResponse(Hub.Version, DataChangeStatus.Committed, null);
    }
}
