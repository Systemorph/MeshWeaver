using System.Collections.Immutable;
using System.Reflection;
using MeshWeaver.Collections;
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

    IReadOnlyDictionary<StreamIdentity, ISynchronizationStream<EntityStore>> Streams { get; }
    ISynchronizationStream<EntityStore> GetStream(WorkspaceReference<EntityStore> reference);
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

 
    IReadOnlyDictionary<StreamIdentity,ISynchronizationStream<EntityStore>> IDataSource.Streams => Streams;

    protected ImmutableDictionary<StreamIdentity, ISynchronizationStream<EntityStore>> Streams { get; set; } =
        ImmutableDictionary<StreamIdentity, ISynchronizationStream<EntityStore>>.Empty;

    public CollectionsReference Reference => GetReference();

    protected virtual CollectionsReference GetReference() =>
        new CollectionsReference(TypeSources.Values.Select(ts => ts.CollectionName).ToArray());

    public virtual ValueTask DisposeAsync()
    {
        foreach (var stream in Streams.Values)
            stream.Dispose();

        if (changesSubscriptions != null)
            foreach (var subscription in changesSubscriptions)
                subscription.Dispose();
        return default;
    }
    public virtual ISynchronizationStream<EntityStore> GetStream(WorkspaceReference<EntityStore> reference)
    {
        StreamIdentity streamIdentity = reference is PartitionedCollectionsReference partitioned
            ? new(Id, partitioned.Partition)
            : new(Id, null);

        var stream = Streams.GetOrAdd(streamIdentity, CreateStream);
        if (stream.Reference.Equals(reference))
            return stream;
        return (ISynchronizationStream<EntityStore>)stream.Reduce(reference);
    }

    protected abstract ISynchronizationStream<EntityStore> CreateStream(StreamIdentity identity);

    protected virtual ISynchronizationStream<EntityStore> SetupDataSourceStream(StreamIdentity identity)
    {
        var reference = GetReference();


        var stream = new SynchronizationStream<EntityStore, WorkspaceReference>(
            identity,
            Hub.Address,
            Hub,
            reference,
            Workspace.ReduceManager.ReduceTo<EntityStore>()
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
                        : new PartitionedCollectionReference(
                            stream.StreamIdentity.Partition,
                            new(ts.CollectionName)
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
        var stream = SetupDataSourceStream(identity);
        Streams = Streams.SetItem(stream.StreamIdentity, stream);
        stream.InitializeAsync(cancellationToken => GetInitialValue(stream, cancellationToken));
        return stream;
    }


}
