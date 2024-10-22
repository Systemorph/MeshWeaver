using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reflection;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Messaging;
using MeshWeaver.Reflection;

namespace MeshWeaver.Data;

public interface IDataSource : IAsyncDisposable
{
    IReadOnlyDictionary<Type, ITypeSource> TypeSources { get; }
    IReadOnlyCollection<Type> MappedTypes { get; }
    object Id { get; }
    CollectionsReference Reference { get; }
    void Initialize();

    ISynchronizationStream<EntityStore> GetStream(WorkspaceReference<EntityStore> reference);

    ISynchronizationStream<EntityStore> GetStream(object partition);
}

public abstract record DataSource<TDataSource>(object Id, IWorkspace Workspace) : IDataSource
    where TDataSource : DataSource<TDataSource>
{
    protected virtual TDataSource This => (TDataSource)this;
    protected IMessageHub Hub => Workspace.Hub;

    IReadOnlyDictionary<Type, ITypeSource> IDataSource.TypeSources => TypeSources;

    protected ImmutableDictionary<Type, ITypeSource> TypeSources { get; init; } =
        ImmutableDictionary<Type, ITypeSource>.Empty;

    public TDataSource WithTypeSource(Type type, ITypeSource typeSource) =>
        This with
        {
            TypeSources = TypeSources.SetItem(type, typeSource)
        };

    public IReadOnlyCollection<Type> MappedTypes => TypeSources.Keys.ToArray();

    public ITypeSource GetTypeSource(string collectionName) =>
        TypeSources.Values.FirstOrDefault(x => x.CollectionName == collectionName);

    public ITypeSource GetTypeSource(Type type) => TypeSources.GetValueOrDefault(type);

    public virtual TDataSource WithType(Type type, Func<ITypeSource, ITypeSource> config) =>
        (TDataSource)WithTypeMethod.MakeGenericMethod(type).InvokeAsFunction(this, config);

    private static readonly MethodInfo WithTypeMethod = ReflectionHelper.GetMethodGeneric<
        DataSource<TDataSource>
    >(x => x.WithType<object>(default));

    public TDataSource WithType<T>()
        where T : class => WithType<T>(d => d);

    public TDataSource WithTypes(IEnumerable<Type> types) =>
        types.Aggregate(This, (ds, t) => ds.WithType(t, x => x));

    protected abstract TDataSource WithType<T>(Func<ITypeSource, ITypeSource> config)
        where T : class;

    private IReadOnlyCollection<IDisposable> changesSubscriptions;



    protected readonly ConcurrentDictionary<object, ISynchronizationStream<EntityStore>> Streams = new();

    public CollectionsReference Reference => GetReference();

    protected virtual CollectionsReference GetReference() =>
        new CollectionsReference(TypeSources.Values.Select(ts => ts.CollectionName).ToArray());

    public virtual async ValueTask DisposeAsync()
    {
        foreach (var stream in Streams.Values)
            await stream.DisposeAsync();

        if (changesSubscriptions != null)
            foreach (var subscription in changesSubscriptions)
                subscription.Dispose();
    }
    public virtual ISynchronizationStream<EntityStore> GetStream(WorkspaceReference<EntityStore> reference)
    {
        var stream = GetStream(reference is IPartitionedWorkspaceReference partitioned ? partitioned.Partition : null);
        return stream.Reduce(reference);
    }


    public ISynchronizationStream<EntityStore> GetStream(object partition)
    {
        var identity = new StreamIdentity(Id, partition);
        return Streams.GetOrAdd(partition ?? Id, _ => CreateStream(identity));
    }

    protected abstract ISynchronizationStream<EntityStore> CreateStream(StreamIdentity identity);

    protected virtual ISynchronizationStream<EntityStore> SetupDataSourceStream(StreamIdentity identity)
    {
        var reference = GetReference();


        var stream = new SynchronizationStream<EntityStore>(
            identity,
            Hub,
            reference,
            Workspace.ReduceManager.ReduceTo<EntityStore>(),
            conf => conf
        );

        return stream;
    }

    public virtual void Initialize()
    {
    }
}

public record GenericDataSource(object Id, IWorkspace Workspace)
    : GenericDataSource<GenericDataSource>(Id, Workspace);

public record GenericDataSource<TDataSource>(object Id, IWorkspace Workspace)
    : TypeSourceBasedDataSource<TDataSource>(Id, Workspace)
    where TDataSource : GenericDataSource<TDataSource>
{
    protected override TDataSource WithType<T>(Func<ITypeSource, ITypeSource> config) =>
        WithType<T>(x => (TypeSourceWithType<T>)config(x));

    public TDataSource WithType<T>(Func<TypeSourceWithType<T>, TypeSourceWithType<T>> configurator)
        where T : class => WithTypeSource(typeof(T), configurator.Invoke(new(Workspace, Id)));
}

public abstract record TypeSourceBasedDataSource<TDataSource>(object Id, IWorkspace Workspace)
    : DataSource<TDataSource>(Id, Workspace)
    where TDataSource : TypeSourceBasedDataSource<TDataSource>
{
    public override void Initialize()
    {
        base.Initialize();
        GetStream(GetReference());
    }


    protected virtual void Synchronize(ChangeItem<EntityStore> item)
    {
        foreach (var typeSource in TypeSources.Values)
            typeSource.Update(item);
    }

    protected virtual async Task<EntityStore>
        GetInitialValue(ISynchronizationStream<EntityStore> stream,
            CancellationToken cancellationToken)
    {
        var initial = await TypeSources
            .Values.ToAsyncEnumerable()
            .SelectAwait(async ts =>
            {
                WorkspaceReference<InstanceCollection> reference =
                    stream.StreamIdentity.Partition == null
                        ? new CollectionReference(ts.CollectionName)
                        : new PartitionedWorkspaceReference<InstanceCollection>(
                            stream.StreamIdentity.Partition,
                            new CollectionReference(ts.CollectionName)
                        );
                return new
                {
                    Reference = reference,
                    Initialized = await ts.InitializeAsync(
                        reference,
                        cancellationToken
                    )
                };
            })
            .AggregateAsync(
                new EntityStore()
                {
                    GetCollectionName = Workspace.DataContext.TypeRegistry.GetOrAddType
                },
                (store, selected) => store.Update(selected.Reference, selected.Initialized),
                cancellationToken: cancellationToken
            );
        return initial;
    }

    protected override ISynchronizationStream<EntityStore> CreateStream(StreamIdentity identity)
    {
        return SetupDataSourceStream(identity);
    }

    protected override ISynchronizationStream<EntityStore> SetupDataSourceStream(StreamIdentity identity)
    {
        var stream = base.SetupDataSourceStream(identity);
        stream.Initialize(cancellationToken => GetInitialValue(stream, cancellationToken));
        stream.AddDisposable(stream.Skip(1).Where(x => x.ChangedBy is not null && !x.ChangedBy.Equals(Id)).Subscribe(Synchronize));
        return stream;
    }
}
