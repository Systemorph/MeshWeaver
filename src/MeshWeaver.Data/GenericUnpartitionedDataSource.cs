using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reflection;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Messaging;
using MeshWeaver.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Data;

public record DataSourceAddress(string Id) : Address(TypeName, Id)
{
    public const string TypeName = "ds";
}

public interface IDataSource : IDisposable
{
    ITypeSource? GetTypeSource(Type type);
    ITypeSource? GetTypeSource(string collectionName);
    IReadOnlyCollection<Type> MappedTypes { get; }
    object Id { get; }
    CollectionsReference Reference { get; }

    ISynchronizationStream<EntityStore> GetStream(WorkspaceReference<EntityStore> reference);

    ISynchronizationStream<EntityStore>? GetStreamForPartition(object? partition);
    IEnumerable<ITypeSource> TypeSources { get; }

    internal Task Initialized { get; }
    internal void Initialize();
}

public interface IUnpartitionedDataSource : IDataSource
{
    IUnpartitionedDataSource WithType(Type type, Func<ITypeSource, ITypeSource>? config = null);
    IUnpartitionedDataSource WithType<T>(Func<ITypeSource, ITypeSource>? config = null) where T : class;
}
public interface IPartitionedDataSource<in TPartition> : IDataSource
{
    IPartitionedDataSource<TPartition> WithType<T>(Func<T, TPartition> partitionFunction, Func<IPartitionedTypeSource, IPartitionedTypeSource>? config = null) where T : class;
}


public abstract record PartitionedDataSource<TDataSource, TTypeSource, TPartition>(object Id, IWorkspace Workspace)
    : DataSource<TDataSource, TTypeSource>(Id, Workspace), IPartitionedDataSource<TPartition>
    where TDataSource : PartitionedDataSource<TDataSource, TTypeSource, TPartition>
    where TTypeSource : IPartitionedTypeSource
{

    public abstract TDataSource WithType<T>(Func<T, TPartition> partitionFunction, Func<TTypeSource, TTypeSource> config)
        where T : class;
    IPartitionedDataSource<TPartition> IPartitionedDataSource<TPartition>.WithType<T>(Func<T, TPartition> partitionFunction, Func<IPartitionedTypeSource, IPartitionedTypeSource>? config) =>
        WithType(partitionFunction, ts => (TTypeSource)(config ?? (x => x)).Invoke(ts));


}

public abstract record UnpartitionedDataSource<TDataSource, TTypeSource>(object Id, IWorkspace Workspace)
    : DataSource<TDataSource, TTypeSource>(Id, Workspace), IUnpartitionedDataSource
    where TDataSource : UnpartitionedDataSource<TDataSource, TTypeSource>
    where TTypeSource : ITypeSource
{
    public virtual IUnpartitionedDataSource WithType(Type type, Func<ITypeSource, ITypeSource>? config) =>
        (TDataSource)WithTypeMethod.MakeGenericMethod(type).InvokeAsFunction(this, config ?? (x => x));

    private static readonly MethodInfo WithTypeMethod = ReflectionHelper.GetMethodGeneric<
        UnpartitionedDataSource<TDataSource, TTypeSource>
    >(x => x.WithType<object>(default));

    public TDataSource WithType<T>()
        where T : class => WithType<T>(d => d);
    public abstract TDataSource WithType<T>(Func<ITypeSource, ITypeSource>? config)
        where T : class;
    IUnpartitionedDataSource IUnpartitionedDataSource.WithType<T>(Func<ITypeSource, ITypeSource>? config) =>
        WithType<T>(config ?? (x => x));

    public IUnpartitionedDataSource WithTypes(IEnumerable<Type> types) =>
        types.Aggregate((IUnpartitionedDataSource)This, (ds, t) => ds.WithType(t, x => x));

}


public abstract record DataSource<TDataSource, TTypeSource>(object Id, IWorkspace Workspace) : IDataSource
    where TDataSource : DataSource<TDataSource, TTypeSource>
    where TTypeSource : ITypeSource
{
    protected virtual TDataSource This => (TDataSource)this;
    protected IMessageHub Hub => Workspace.Hub;
    protected ILogger Logger => Workspace.Hub.ServiceProvider.GetRequiredService<ILogger<TDataSource>>();

    IEnumerable<ITypeSource> IDataSource.TypeSources => TypeSources.Values.Cast<ITypeSource>();

    protected ImmutableDictionary<Type, TTypeSource> TypeSources { get; init; } =
        ImmutableDictionary<Type, TTypeSource>.Empty;

    public TDataSource WithTypeSource(Type type, TTypeSource typeSource) =>
        This with
        {
            TypeSources = TypeSources.SetItem(type, typeSource)
        };

    public IReadOnlyCollection<Type> MappedTypes => TypeSources.Keys.ToArray();

    public ITypeSource? GetTypeSource(string collectionName) =>
        TypeSources.Values.FirstOrDefault(x => x.CollectionName == collectionName);

    public ITypeSource? GetTypeSource(Type type) => TypeSources.GetValueOrDefault(type);


    private readonly IReadOnlyCollection<IDisposable>? changesSubscriptions;



    protected readonly Dictionary<object, ISynchronizationStream<EntityStore>> Streams = new();

    public Task Initialized
    {
        get
        {
            lock (Streams)
                return Task.WhenAll(Streams.Values.Select(s => s.Hub.Started));
        }
    }
    public CollectionsReference Reference => GetReference();

    protected virtual CollectionsReference GetReference() =>
        new(TypeSources.Values.Select(ts => ts.CollectionName).ToArray());

    public virtual void Dispose()
    {
        foreach (var stream in Streams.Values)
            stream.Dispose();

        if (changesSubscriptions != null)
            foreach (var subscription in changesSubscriptions)
                subscription.Dispose();
    }
    public virtual ISynchronizationStream<EntityStore> GetStream(WorkspaceReference<EntityStore> reference)
    {
        var stream = GetStreamForPartition(reference is IPartitionedWorkspaceReference partitioned ? partitioned.Partition : null);
        return stream.Reduce(reference) ?? throw new InvalidOperationException("Unable to create stream");
    }

    public ISynchronizationStream<EntityStore> GetStreamForPartition(object? partition)
    {
        var identity = new StreamIdentity(new DataSourceAddress(Id.ToString() ?? ""), partition);
        lock (Streams)
        {
            if (Streams.TryGetValue(partition ?? Id, out var ret))
                return ret;
            Logger.LogDebug("Creating new stream for Id {Id} and Partition {Partition}", Id, partition);
            Streams[partition ?? Id] = ret = CreateStream(identity);
            return ret;
        }
    }

    protected abstract ISynchronizationStream<EntityStore> CreateStream(StreamIdentity identity);

    protected virtual ISynchronizationStream<EntityStore> CreateStream(StreamIdentity identity,
        Func<StreamConfiguration<EntityStore>, StreamConfiguration<EntityStore>> config)
        => SetupDataSourceStream(identity, config);

    protected virtual ISynchronizationStream<EntityStore> SetupDataSourceStream(StreamIdentity identity,
        Func<StreamConfiguration<EntityStore>, StreamConfiguration<EntityStore>> config)
    {
        var reference = GetReference();


        var stream = new SynchronizationStream<EntityStore>(
            identity,
            Hub,
            reference,
            Workspace.ReduceManager.ReduceTo<EntityStore>(),
            config
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

public abstract record GenericUnpartitionedDataSource<TDataSource>(object Id, IWorkspace Workspace)
    : TypeSourceBasedUnpartitionedDataSource<TDataSource, ITypeSource>(Id, Workspace)
    where TDataSource : GenericUnpartitionedDataSource<TDataSource>
{

    public override TDataSource WithType<T>(Func<ITypeSource, ITypeSource>? config) =>
        WithType<T>(x => (TypeSourceWithType<T>)(config ?? (y => y))(x));

    public TDataSource WithType<T>(Func<TypeSourceWithType<T>, TypeSourceWithType<T>>? configurator)
        where T : class => WithTypeSource(typeof(T), (configurator ?? (x => x)).Invoke(new(Workspace, Id)));
}
public abstract record GenericPartitionedDataSource<TPartition>(object Id, IWorkspace Workspace)
    : GenericPartitionedDataSource<GenericPartitionedDataSource<TPartition>, TPartition>(Id, Workspace)
{
    public ISynchronizationStream<EntityStore> GetStream()
        => GetStreamForPartition(null);

}

public abstract record GenericPartitionedDataSource<TDataSource, TPartition>(object Id, IWorkspace Workspace)
    : TypeSourceBasedPartitionedDataSource<TDataSource, IPartitionedTypeSource, TPartition>(Id, Workspace)
    where TDataSource : GenericPartitionedDataSource<TDataSource, TPartition>
{
    public override TDataSource WithType<T>(Func<T, TPartition> partitionFunction, Func<IPartitionedTypeSource, IPartitionedTypeSource>? config)
        => WithTypeSource(typeof(T), (config ?? (x => x)).Invoke(new PartitionedTypeSourceWithType<T, TPartition>(Workspace, partitionFunction, Id)));
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

    protected virtual async Task<EntityStore> GetInitialValueAsync(ISynchronizationStream<EntityStore> stream, CancellationToken cancellationToken)
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
                    GetCollectionName = valueType => Workspace.DataContext.TypeRegistry.GetOrAddType(valueType, valueType.Name)
                },
                (store, selected) => store.Update(selected.Reference, selected.Initialized),
                cancellationToken: cancellationToken
            );
        return initial;
    }


    protected override ISynchronizationStream<EntityStore> CreateStream(StreamIdentity identity)
    {
        return CreateStream(identity,
            config => config.WithInitialization(GetInitialValueAsync).WithExceptionCallback(LogException));
    }

    private Task LogException(Exception exception)
    {
        Logger.LogError("An exception occurred synchronizing Data Source {Identity}: {Exception}", this.Id, exception);
        return Task.CompletedTask;
    }

    protected override ISynchronizationStream<EntityStore> SetupDataSourceStream(StreamIdentity identity, Func<StreamConfiguration<EntityStore>, StreamConfiguration<EntityStore>> config)
    {
        var stream = base.SetupDataSourceStream(identity, config);

        var isFirst = true;
        stream.RegisterForDisposal(
            stream
                .Synchronize()
                .Where(x => isFirst || (x.ChangedBy is not null && !x.ChangedBy.Equals(Id)))
                .Subscribe(change =>
                {
                    if (isFirst)
                    {
                        isFirst = false;
                        return; // Skip processing on first emission (initialization)
                    }
                    Synchronize(change);
                })
        );
        // Always use async initialization to call GetInitialValueAsync properly

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
                    GetCollectionName = valueType => Workspace.DataContext.TypeRegistry.GetOrAddType(valueType, valueType.Name)
                },
                (store, selected) => store.Update(selected.Reference, selected.Initialized),
                cancellationToken: cancellationToken
            );
        return initial;
    }

    protected override ISynchronizationStream<EntityStore> CreateStream(StreamIdentity identity, Func<StreamConfiguration<EntityStore>, StreamConfiguration<EntityStore>> config)
    {
        return SetupDataSourceStream(identity, config);
    }

    protected override ISynchronizationStream<EntityStore> SetupDataSourceStream(StreamIdentity identity, Func<StreamConfiguration<EntityStore>, StreamConfiguration<EntityStore>> config)
    {
        var stream = base.SetupDataSourceStream(identity, config);

        // Always use async initialization to call GetInitialValueAsync properly

        var isFirst = true;
        stream.RegisterForDisposal(
            stream
                .Synchronize()
                .Where(x => isFirst || (x.ChangedBy is not null && !x.ChangedBy.Equals(Id)))
                .Subscribe(change =>
                {
                    if (isFirst)
                    {
                        isFirst = false;
                        return; // Skip processing on first emission (initialization)
                    }
                    Synchronize(change);
                })
        );
        return stream;
    }
}
