using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Data.Serialization;
using OpenSmc.Messaging;
using OpenSmc.Reflection;

namespace OpenSmc.Data;

public interface IDataSource : IAsyncDisposable
{
    IReadOnlyDictionary<Type, ITypeSource> TypeSources { get; }
    IReadOnlyCollection<Type> MappedTypes { get; }
    object Id { get; }
    void Initialize();
    ValueTask<EntityStore> Initialized { get; }
}

public abstract record DataSource<TDataSource>(object Id, IMessageHub Hub) : IDataSource
    where TDataSource : DataSource<TDataSource>
{
    protected readonly IWorkspace Workspace = Hub.ServiceProvider.GetRequiredService<IWorkspace>();

    protected virtual TDataSource This => (TDataSource)this;

    protected ImmutableList<ISynchronizationStream<EntityStore>> Streams { get; set; } = [];

    public ValueTask<EntityStore> Initialized =>
        Streams
            .ToAsyncEnumerable()
            .SelectAwait(async stream => await stream.Initialized)
            .AggregateAsync(new EntityStore(), (store, el) => store.Merge(el));

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

    protected abstract TDataSource WithType<T>(Func<ITypeSource, ITypeSource> config)
        where T : class;

    private IReadOnlyCollection<IDisposable> changesSubscriptions;

    public virtual void Initialize()
    {
        var reference = GetReference();
        var stream = Workspace.GetStream(Hub.Address, reference);
        Streams = Streams.Add(stream);
        stream.Skip(1).Subscribe(Synchronize);
        Hub.Schedule(cancellationToken => InitializeAsync(stream, cancellationToken));
    }

    protected virtual void Synchronize(ChangeItem<EntityStore> item)
    {
        if (item.ChangedBy == null || Id.Equals(item.ChangedBy))
            return;

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
                new EntityStore(),
                (store, selected) => store.Merge(selected.Reference, selected.Initialized), cancellationToken: cancellationToken);

        stream.OnNext(new ChangeItem<EntityStore>(stream.Owner, stream.Reference, initial, Id, null, Hub.Version));
    }

    protected virtual WorkspaceReference<EntityStore> GetReference()
    {
        WorkspaceReference<EntityStore> collections = new CollectionsReference(
            TypeSources.Values.Select(ts => ts.CollectionName).ToArray()
        );
        return collections;
    }

    public virtual ValueTask DisposeAsync()
    {
        foreach (var stream in Streams)
            stream.Dispose();

        if (changesSubscriptions != null)
            foreach (var subscription in changesSubscriptions)
                subscription.Dispose();
        return default;
    }
}

public record GenericDataSource(object Id, IMessageHub Hub)
    : GenericDataSource<GenericDataSource>(Id, Hub) { }

public record GenericDataSource<TDataSource>(object Id, IMessageHub Hub)
    : DataSource<TDataSource>(Id, Hub)
    where TDataSource : GenericDataSource<TDataSource>
{
    protected override TDataSource WithType<T>(Func<ITypeSource, ITypeSource> config) =>
        WithType<T>(x => (TypeSourceWithType<T>)config(x));

    public TDataSource WithType<T>(Func<TypeSourceWithType<T>, TypeSourceWithType<T>> configurator)
        where T : class => WithTypeSource(typeof(T), configurator.Invoke(new(Hub, Id)));
}
