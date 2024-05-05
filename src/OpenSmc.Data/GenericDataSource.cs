using System.Collections.Immutable;
using System.Net.WebSockets;
using System.Reactive.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Data.Serialization;
using OpenSmc.Messaging;
using OpenSmc.Reflection;

namespace OpenSmc.Data;

public interface IDataSource : IAsyncDisposable
{
    IEnumerable<ITypeSource> TypeSources { get; }
    IReadOnlyCollection<Type> MappedTypes { get; }
    object Id { get; }
    IReadOnlyCollection<DataChangeRequest> Change(DataChangeRequest request);
    IEnumerable<ChangeStream<EntityStore>> Initialize();
}

public abstract record DataSource<TDataSource>(object Id, IMessageHub Hub) : IDataSource
    where TDataSource : DataSource<TDataSource>
{
    protected readonly IWorkspace Workspace = Hub.ServiceProvider.GetRequiredService<IWorkspace>();

    protected virtual TDataSource This => (TDataSource)this;

    IEnumerable<ITypeSource> IDataSource.TypeSources => TypeSources.Values;

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

    public virtual IReadOnlyCollection<DataChangeRequest> Change(DataChangeRequest request)
    {
        if (request is DataChangeRequestWithElements requestWithElements)
            return Change(requestWithElements);

        throw new ArgumentOutOfRangeException(
            $"No implementation found for {request.GetType().FullName}"
        );
    }

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

    public virtual IEnumerable<ChangeStream<EntityStore>> Initialize()
    {
        //return TypeSources.Values.Select(ts => ts.InitializeAsync(workspaceStream));
        var ret = GetInitialChangeStream().ToArray();
        Hub.Schedule(async cancellationToken =>
        {
            var instances = (
                await TypeSources
                    .Values.ToAsyncEnumerable()
                    .SelectAwait(async ts => new
                    {
                        TypeSource = ts,
                        Instances = await ts.InitializeAsync(cancellationToken)
                    })
                    .ToArrayAsync(cancellationToken: cancellationToken)
            ).ToImmutableDictionary(x => x.TypeSource.CollectionName, x => x.Instances);

            foreach (var changeStream in ret)
                changeStream.Initialize(new() { Collections = instances });
        });

        return ret;
    }

    protected virtual IEnumerable<ChangeStream<EntityStore>> GetInitialChangeStream()
    {
        changesSubscriptions = TypeSources
            .Values.Select(ts =>
                Workspace
                    .Stream.Skip(1)
                    .Where(x => !x.ChangedBy.Equals(Id))
                    .Subscribe(ws => ts.Update(ws))
            )
            .ToArray();
        yield return Workspace.GetRawStream(Id, new WorkspaceStoreReference());
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
