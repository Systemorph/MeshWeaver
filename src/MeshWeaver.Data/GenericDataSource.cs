﻿using System.Collections.Immutable;
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

    IReadOnlyDictionary<StreamReference, ISynchronizationStream<EntityStore>> Streams { get; }
    Task Initialized { get; }
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

 
    IReadOnlyDictionary<StreamReference,ISynchronizationStream<EntityStore>> IDataSource.Streams => Streams;
    public Task Initialized => Task.WhenAll(Streams.Values.Select(s => s.Initialized));

    protected ImmutableDictionary<StreamReference, ISynchronizationStream<EntityStore>> Streams { get; set; } =
        ImmutableDictionary<StreamReference, ISynchronizationStream<EntityStore>>.Empty;

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

    public virtual void Initialize()
    {
        foreach (var stream in Streams.Values)
            stream.Subscribe(Workspace.Stream);
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
        var stream = SetupDataSourceStream();
        Streams = Streams.SetItem(stream.StreamReference, stream);
        Hub.InvokeAsync(cancellationToken => InitializeAsync(stream, cancellationToken));
        base.Initialize();
    }

    protected virtual ISynchronizationStream<EntityStore> SetupDataSourceStream()
    {
        var reference = GetReference();

        var workspaceSync = Workspace.Stream.Reduce(reference, Id);

        var stream = new SynchronizationStream<EntityStore, WorkspaceReference>(
            new(Id,null),
            Hub.Address,
            Hub,
            reference,
            workspaceSync.ReduceManager.ReduceTo<EntityStore>(),
            InitializationMode.Automatic
        );

        stream.AddDisposable(
            workspaceSync.Skip(1).Where(x => !Id.Equals(x.ChangedBy)).Subscribe(Synchronize)
        );
        stream.AddDisposable(workspaceSync);
        return stream;
    }

    protected virtual void Synchronize(ChangeItem<EntityStore> item)
    {
        foreach (var typeSource in TypeSources.Values)
            typeSource.Update(item);
    }

    private async Task InitializeAsync(
        ISynchronizationStream<EntityStore> stream,
        CancellationToken cancellationToken
    )
    {
        var initial = await TypeSources
            .Values.ToAsyncEnumerable()
            .SelectAwait(async ts => new
            {
                Reference = new CollectionReference(ts.CollectionName),
                Initialized = await ts.InitializeAsync(
                    new CollectionReference(ts.CollectionName),
                    cancellationToken
                )
            })
            .AggregateAsync(
                new EntityStore()
                {
                    GetCollectionName = Workspace.DataContext.TypeRegistry.GetOrAddType
                },
                (store, selected) => store.Update(selected.Reference, selected.Initialized),
                cancellationToken: cancellationToken
            );

        stream.Initialize(
            new ChangeItem<EntityStore>(
                stream.Owner,
                stream.Reference,
                initial,
                Id,
                null,
                Hub.Version
            )
        );
    }
}
