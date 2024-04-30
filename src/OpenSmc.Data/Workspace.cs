using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenSmc.Data.Serialization;
using OpenSmc.Messaging;

namespace OpenSmc.Data;

public class Workspace(IMessageHub hub, object id) : IWorkspace
{
    [ActivatorUtilitiesConstructor]
    public Workspace(IMessageHub hub)
        : this(hub, hub.Address) { }

    private WorkspaceState State { get; set; }
    public IObservable<ChangeItem<WorkspaceState>> Stream => synchronizationStream;
    private readonly ReplaySubject<ChangeItem<WorkspaceState>> synchronizationStream = new(1);
    private readonly Subject<ChangeItem<WorkspaceState>> subject = new();
    public IObservable<ChangeItem<WorkspaceState>> ChangeStream => changeStream;
    private readonly Dictionary<string, ITypeSource> typeSources = new();

    public IReadOnlyCollection<Type> MappedTypes => State.MappedTypes.ToArray();
    private readonly ConcurrentDictionary<
        (object Address, WorkspaceReference Reference),
        IDisposable
    > streams = new();

    private readonly ConcurrentDictionary<
        (object Address, WorkspaceReference Reference),
        IDisposable
    > externalSubscriptions = new();

    private void RegisterChangeFeed<TReference>(
        object address,
        WorkspaceReference<TReference> reference
    )
    {
        if (externalSubscriptions.ContainsKey((address, reference)))
            return;
        var stream =
            (ChangeStream<TReference>)
                streams.GetOrAdd(
                    (Hub.Address, reference),
                    key =>
                    {
                        return new ChangeStream<TReference>(
                            this,
                            key.Address,
                            reference,
                            Hub,
                            () => Hub.Version,
                            false
                        );
                    }
                );
        stream.Disposables.Add(
            externalSynchronizationStream
                .Where(x => x.Address.Equals(address) && x.Reference.Equals(reference))
                .Subscribe(stream)
        );
        externalSubscriptions.GetOrAdd(
            (address, reference),
            _ => stream.Subscribe<DataChangedEvent>(dc => Hub.Post(dc, o => o.WithTarget(address)))
        );
    }

    public ChangeStream<TReference> GetRemoteStream<TReference>(
        object address,
        WorkspaceReference<TReference> reference
    )
    {
        return GetRemoteStreamImpl(address, reference);
    }

    internal ChangeStream<TReference> GetRemoteStreamImpl<TReference>(
        object address,
        WorkspaceReference<TReference> reference
    )
    {
        if (Hub.Address.Equals(address))
            throw new ArgumentException(
                $"This method provides access to external hubs. Address {address} is our own address."
            );

        var key = (address, reference);
        return (ChangeStream<TReference>)
            streams.GetOrAdd(
                key,
                _ =>
                {
                    Hub.Post(new SubscribeRequest(reference), o => o.WithTarget(address));
                    var ret = new ChangeStream<TReference>(
                        this,
                        address,
                        reference,
                        Hub,
                        () => Hub.Version,
                        true
                    );

                    ret.Disposables.Add(
                        ((IObservable<PatchChangeRequest>)ret)
                            .Where(x => x.Address.Equals(address) && x.Reference.Equals(reference))
                            .Subscribe(patch =>
                                Hub.RegisterCallback(
                                    Hub.Post(patch, o => o.WithTarget(address)),
                                    HandleCommitResponse
                                )
                            )
                    );
                    ret.Disposables.Add(
                        ret.Subscribe<DataChangedEvent>(dataChanged =>
                            Hub.Post(dataChanged, o => o.WithTarget(address))
                        )
                    );

                    //ret.Disposables.Add(ret.Subscribe<ChangeItem<TReference>>(Synchronize));

                    ret.Disposables.Add(
                        externalSynchronizationStream
                            .Where(x => x.Address.Equals(address) && x.Reference.Equals(reference))
                            .DistinctUntilChanged()
                            .Subscribe(ret)
                    );

                    ret.Disposables.Add(
                        new Disposables.AnonymousDisposable(
                            () =>
                                Hub.Post(
                                    new UnsubscribeDataRequest(reference),
                                    o => o.WithTarget(address)
                                )
                        )
                    );
                    return ret;
                }
            );
    }

    public void Update(IEnumerable<object> instances, UpdateOptions updateOptions) =>
        RequestChange(new UpdateDataRequest(instances.ToArray()) { Options = updateOptions }, null);

    public void Delete(IEnumerable<object> instances) =>
        RequestChange(new DeleteDataRequest(instances.ToArray()), null);

    private readonly TaskCompletionSource initializeTaskCompletionSource = new();
    public Task Initialized => initializeTaskCompletionSource.Task;
    private DataContext DataContext { get; set; }

    private ReduceManager ReduceManager { get; } = new();
    private WorkspaceState LastCommitted { get; set; }
    public IMessageHub Hub { get; } = hub;
    public object Id { get; } = id;
    private ILogger logger = hub.ServiceProvider.GetRequiredService<ILogger<Workspace>>();

    WorkspaceState IWorkspace.State => State;

    public void Initialize() // This loads the persisted state
    {
        DataContext = Hub.GetDataConfiguration(ReduceManager);
        Initialize(DataContext);
    }

    private readonly Subject<ChangeItem<WorkspaceState>> changeStream = new();

    private void Initialize(DataContext dataContext)
    {
        logger.LogDebug($"Starting data plugin at address {Id}");
        var dataContextStreams = dataContext.Initialize().ToArray();

        logger.LogDebug("Initialized workspace in address {address}", Id);

        foreach (var ts in DataContext.DataSources.Values.SelectMany(ds => ds.TypeSources))
            typeSources[ts.CollectionName] = ts;

        State = CreateState(new EntityStore());

        var initializeObserver = new InitializeObserver(
            dataContextStreams.ToDictionary(x => x.Address),
            () =>
            {
                subject.Subscribe(synchronizationStream);
                subject.OnNext(
                    new ChangeItem<WorkspaceState>(Id, new EntireWorkspace(), State, Id)
                );
                LastCommitted = State;

                initializeTaskCompletionSource.SetResult();
            }
        );

        foreach (var stream in dataContextStreams)
        {
            streams[(stream.Address, stream.Reference)] = stream;
            stream.Disposables.Add(stream.Subscribe<ChangeItem<EntityStore>>(Synchronize));
            initializeObserver.Disposables.Add(stream.Subscribe(initializeObserver));
        }
    }

    public WorkspaceState CreateState(EntityStore entityStore)
    {
        return new(Hub, entityStore, typeSources, ReduceManager.Reduce);
    }

    private void Synchronize(ChangeItem<EntityStore> item)
    {
        if (Hub.Address.Equals(item.ChangedBy))
            return;

        State = State.Synchronize(item);
    }

    public void Commit()
    {
        subject.OnNext(new ChangeItem<WorkspaceState>(Id, new EntireWorkspace(), State, Id));
        LastCommitted = State;
    }

    public void Rollback()
    {
        State = LastCommitted;
    }

    public IObservable<ChangeItem<TStream>> GetStream<TStream>(
        WorkspaceReference<TStream> reference
    ) => ReduceManager.ReduceStream(Stream, reference);

    private readonly Subject<DataChangedEvent> externalSynchronizationStream = new();

    public void RequestChange(DataChangeRequest change, object changedBy)
    {
        State = State.Change(change) with { Version = Hub.Version };
        changeStream.OnNext(
            new ChangeItem<WorkspaceState>(Id, new EntireWorkspace(), State, changedBy)
        );
    }

    public void Synchronize(DataChangedEvent dataChangedEvent)
    {
        externalSynchronizationStream.OnNext(dataChangedEvent);
        Commit();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var subscription in externalSubscriptions.Values)
            subscription.Dispose();

        await DataContext.DisposeAsync();
    }

    protected IMessageDelivery HandleCommitResponse(IMessageDelivery<DataChangeResponse> response)
    {
        if (response.Message.Status == DataChangeStatus.Committed)
            return response.Processed();
        // TODO V10: Here we have to put logic to revert the state if commit has failed. (26.02.2024, Roland Bürgi)
        return response.Ignored();
    }

    public void Subscribe(object sender, WorkspaceReference reference)
    {
        RegisterChangeFeed(sender, (dynamic)reference);
    }

    public void Unsubscribe(object sender, WorkspaceReference reference)
    {
        if (externalSubscriptions.TryRemove((sender, reference), out var existing))
            existing.Dispose();
    }

    public void Update(WorkspaceState state) => State = state;
}
