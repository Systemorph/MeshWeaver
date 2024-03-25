using System.Collections.Immutable;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Messaging;
using OpenSmc.Reflection;
using OpenSmc.Serialization;

namespace OpenSmc.Data;

public interface IDataSource : IAsyncDisposable
{
    IEnumerable<ITypeSource> TypeSources { get; }
    IEnumerable<Type> MappedTypes { get; }
    object Id { get; }
    IReadOnlyCollection<DataChangeRequest> Change(DataChangeRequest request);
    IEnumerable<ChangeStream<EntityStore>> Initialize();
}

public abstract record DataSource<TDataSource>(object Id, IMessageHub Hub) : IDataSource where TDataSource : DataSource<TDataSource>
{
    protected readonly IWorkspace Workspace = Hub.ServiceProvider.GetRequiredService<IWorkspace>();

    protected virtual TDataSource This => (TDataSource)this;

    IEnumerable<ITypeSource> IDataSource.TypeSources => TypeSources.Values;

    protected ImmutableDictionary<Type, ITypeSource> TypeSources { get; init; } = ImmutableDictionary<Type, ITypeSource>.Empty;

    public TDataSource WithTypeSource(Type type, ITypeSource typeSource)
        => This with
        {
            TypeSources = TypeSources.SetItem(type, typeSource)
        };



    public IEnumerable<Type> MappedTypes => TypeSources.Keys;

    public ITypeSource GetTypeSource(string collectionName) =>
        TypeSources.Values.FirstOrDefault(x => x.CollectionName == collectionName);



    public virtual IReadOnlyCollection<DataChangeRequest> Change(DataChangeRequest request)
    {
        if (request is DataChangeRequestWithElements requestWithElements)
            return Change(requestWithElements);

        throw new ArgumentOutOfRangeException($"No implementation found for {request.GetType().FullName}");
    }



    public ITypeSource GetTypeSource(Type type) => TypeSources.GetValueOrDefault(type);


    public virtual TDataSource WithType(Type type, Func<ITypeSource, ITypeSource> config)
        => (TDataSource)WithTypeMethod.MakeGenericMethod(type).InvokeAsFunction(this, config);

    private static readonly MethodInfo WithTypeMethod =
        ReflectionHelper.GetMethodGeneric<DataSource<TDataSource>>(x => x.WithType<object>(default));
    public TDataSource WithType<T>()
        where T : class
        => WithType<T>(d => d);

    protected abstract TDataSource WithType<T>(Func<ITypeSource, ITypeSource> config) where T : class;

    public virtual IEnumerable<ChangeStream<EntityStore>> Initialize()
    {

        //return TypeSources.Values.Select(ts => ts.InitializeAsync(workspaceStream));
        var ret = new ChangeStream<EntityStore>(Id, new EntireWorkspace(), Hub.ServiceProvider.GetRequiredService<ISerializationService>().Options(TypeSources.Values.ToDictionary(x => x.CollectionName)));
        Hub.Schedule(async cancellationToken =>
        {


            var instances = (await TypeSources
                    .Values
                    .ToAsyncEnumerable()
                    .SelectAwait(async ts => new
                        { TypeSource = ts, Instances = await ts.InitializeAsync(cancellationToken) })
                    .ToArrayAsync(cancellationToken: cancellationToken))
                .ToImmutableDictionary(x => x.TypeSource.CollectionName, x => x.Instances);

            ret.Initialize(new(instances));
        });
        return [ret];
    }


    protected ChangeStream<EntityStore> GetStream(object address)
    {
        var reference = GetReference();
        return Workspace.GetChangeStream(address, reference);
    }
    protected virtual WorkspaceReference<EntityStore> GetReference()
    {
        WorkspaceReference<EntityStore> collections = new CollectionsReference
                (
                    TypeSources
                        .Values
                        .Select(ts => ts.CollectionName).ToArray()
                );
        return collections;
    }


    public virtual ValueTask DisposeAsync()
    {
        return default;
    }


}


public record GenericDataSource(object Id, IMessageHub Hub) : DataSource<GenericDataSource>(Id, Hub)
{
    private IDisposable changesSubscription;

    public override ValueTask DisposeAsync()
    {
        changesSubscription?.Dispose();
        return base.DisposeAsync();
    }

    protected override GenericDataSource WithType<T>(Func<ITypeSource, ITypeSource> config)
    => WithType<T>(x => (TypeSourceWithType<T>)config(x));



    public GenericDataSource WithType<T>(
        Func<TypeSourceWithType<T>, TypeSourceWithType<T>> configurator)
        where T : class
        => WithTypeSource(typeof(T), configurator.Invoke(new(Hub, Id)));

    public override IEnumerable<ChangeStream<EntityStore>> Initialize()
    {
        changesSubscription = Workspace.ChangeStream.Subscribe(Update);
        return base.Initialize();
    }

    private void Update(WorkspaceState ws)
    {
        foreach (var ts in TypeSources.Values)
        {
            ts.Update(ws);
        }
    }
}