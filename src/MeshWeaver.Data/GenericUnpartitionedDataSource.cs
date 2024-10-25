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
    ITypeSource GetTypeSource(Type type);
    IReadOnlyCollection<Type> MappedTypes { get; }
    object Id { get; }
    CollectionsReference Reference { get; }
    void Initialize();

    ISynchronizationStream<EntityStore> GetStream(WorkspaceReference<EntityStore> reference);

    ISynchronizationStream<EntityStore> GetStreamForPartition(object partition);
    IEnumerable<ITypeSource> TypeSources { get; }

}

public interface IUnpartitionedDataSource : IDataSource
{
    IUnpartitionedDataSource WithType(Type type, Func<ITypeSource, ITypeSource> config = null);
    IUnpartitionedDataSource WithType<T>(Func<ITypeSource, ITypeSource> config = null) where T : class;
}
public interface IPartitionedDataSource<in TPartition> : IDataSource
{
    IPartitionedDataSource<TPartition> WithType<T>(Func<T,TPartition> partitionFunction, Func<IPartitionedTypeSource, IPartitionedTypeSource> config = null) where T : class;
}


public abstract record PartitionedDataSource<TDataSource, TTypeSource, TPartition>(object Id, IWorkspace Workspace)
    : DataSource<TDataSource, TTypeSource>(Id, Workspace), IPartitionedDataSource<TPartition>
    where TDataSource : PartitionedDataSource<TDataSource, TTypeSource, TPartition>
    where TTypeSource : IPartitionedTypeSource
{

    public abstract TDataSource WithType<T>(Func<T, TPartition> partitionFunction, Func<TTypeSource, TTypeSource> config)
        where T : class;
    IPartitionedDataSource<TPartition> IPartitionedDataSource<TPartition>.WithType<T>(Func<T,TPartition> partitionFunction, Func<IPartitionedTypeSource, IPartitionedTypeSource> config) =>
        WithType(partitionFunction, ts => (TTypeSource)(config ?? (x => x)).Invoke(ts));


}

public abstract record UnpartitionedDataSource<TDataSource, TTypeSource>(object Id, IWorkspace Workspace) 
    : DataSource<TDataSource, TTypeSource>(Id, Workspace), IUnpartitionedDataSource
    where TDataSource : UnpartitionedDataSource<TDataSource, TTypeSource>
    where TTypeSource : ITypeSource
{
    public virtual IUnpartitionedDataSource WithType(Type type, Func<ITypeSource, ITypeSource> config) =>
        (TDataSource)WithTypeMethod.MakeGenericMethod(type).InvokeAsFunction(this, config);

    private static readonly MethodInfo WithTypeMethod = ReflectionHelper.GetMethodGeneric<
        UnpartitionedDataSource<TDataSource, TTypeSource>
    >(x => x.WithType<object>(default));

    public TDataSource WithType<T>()
        where T : class => WithType<T>(d => d);
    public abstract TDataSource WithType<T>(Func<ITypeSource, ITypeSource> config)
        where T : class;
    IUnpartitionedDataSource IUnpartitionedDataSource.WithType<T>(Func<ITypeSource, ITypeSource> config) =>
        WithType<T>(config);

    public IUnpartitionedDataSource WithTypes(IEnumerable<Type> types) =>
        types.Aggregate((IUnpartitionedDataSource)This, (ds, t) => ds.WithType(t, x => x));

}


public abstract record DataSource<TDataSource, TTypeSource>(object Id, IWorkspace Workspace) : IDataSource
    where TDataSource : DataSource<TDataSource, TTypeSource>
    where TTypeSource : ITypeSource
{
    protected virtual TDataSource This => (TDataSource)this;
    protected IMessageHub Hub => Workspace.Hub;

    IEnumerable<ITypeSource> IDataSource.TypeSources => TypeSources.Values.Cast<ITypeSource>();

    protected ImmutableDictionary<Type, TTypeSource> TypeSources { get; init; } =
        ImmutableDictionary<Type, TTypeSource>.Empty;

    public TDataSource WithTypeSource(Type type, TTypeSource typeSource) =>
        This with
        {
            TypeSources = TypeSources.SetItem(type, typeSource)
        };

    public IReadOnlyCollection<Type> MappedTypes => TypeSources.Keys.ToArray();

    public ITypeSource GetTypeSource(string collectionName) =>
        TypeSources.Values.FirstOrDefault(x => x.CollectionName == collectionName);

    public ITypeSource GetTypeSource(Type type) => TypeSources.GetValueOrDefault(type);


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
        var stream = GetStreamForPartition(reference is IPartitionedWorkspaceReference partitioned ? partitioned.Partition : null);
        return stream.Reduce(reference);
    }


    public ISynchronizationStream<EntityStore> GetStreamForPartition(object partition)
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

public record GenericUnpartitionedDataSource(object Id, IWorkspace Workspace)
    : GenericUnpartitionedDataSource<GenericUnpartitionedDataSource>(Id, Workspace)
{
    public ISynchronizationStream<EntityStore> GetStream()
        => GetStreamForPartition(null);

}

public record GenericUnpartitionedDataSource<TDataSource>(object Id, IWorkspace Workspace)
    : TypeSourceBasedUnpartitionedDataSource<TDataSource, ITypeSource>(Id, Workspace)
    where TDataSource : GenericUnpartitionedDataSource<TDataSource>
{
    public override TDataSource WithType<T>(Func<ITypeSource, ITypeSource> config) =>
        WithType<T>(x => (TypeSourceWithType<T>)config(x));

    public TDataSource WithType<T>(Func<TypeSourceWithType<T>, TypeSourceWithType<T>> configurator)
        where T : class => WithTypeSource(typeof(T), configurator.Invoke(new(Workspace, Id)));
}
public record GenericPartitionedDataSource<TPartition>(object Id, IWorkspace Workspace)
    : GenericPartitionedDataSource<GenericPartitionedDataSource<TPartition>, TPartition>(Id, Workspace)
{
    public ISynchronizationStream<EntityStore> GetStream()
        => GetStreamForPartition(null);

}

public record GenericPartitionedDataSource<TDataSource, TPartition>(object Id, IWorkspace Workspace)
    : TypeSourceBasedPartitionedDataSource<TDataSource, IPartitionedTypeSource, TPartition>(Id, Workspace)
    where TDataSource : GenericPartitionedDataSource<TDataSource, TPartition>
{
    public override TDataSource WithType<T>(Func<T, TPartition> partitionFunction, Func<IPartitionedTypeSource, IPartitionedTypeSource> config)
        => WithTypeSource(typeof(T), config.Invoke(new PartitionedTypeSourceWithType<T, TPartition>(Workspace, partitionFunction, Id)));
}

public abstract record TypeSourceBasedUnpartitionedDataSource<TDataSource, TTypeSource>(object Id, IWorkspace Workspace)
    : UnpartitionedDataSource<TDataSource, TTypeSource>(Id, Workspace)
    where TDataSource : TypeSourceBasedUnpartitionedDataSource<TDataSource, TTypeSource>
    where TTypeSource : ITypeSource
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
public abstract record TypeSourceBasedPartitionedDataSource<TDataSource, TTypeSource, TPartition>(object Id, IWorkspace Workspace)
    : PartitionedDataSource<TDataSource, TTypeSource, TPartition>(Id, Workspace)
    where TDataSource : TypeSourceBasedPartitionedDataSource<TDataSource, TTypeSource, TPartition>
    where TTypeSource : IPartitionedTypeSource
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
