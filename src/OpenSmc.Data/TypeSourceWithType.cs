using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Messaging;
using OpenSmc.Reflection;
using OpenSmc.Serialization;

namespace OpenSmc.Data;

public interface ITypeSource : IDisposable
{
    Type ElementType { get; }
    string CollectionName { get; }
    object GetKey(object instance);
    ITypeSource WithKey(Func<object, object> key);
    ITypeSource WithInitialData(Func<CancellationToken, Task<IEnumerable<object>>> loadInstancesAsync);

    ITypeSource WithInitialData(IEnumerable<object> instances)
        => WithInitialData(() => instances);
    ITypeSource WithInitialData(Func<IEnumerable<object>> loadInstances)
        => WithInitialData(_ => Task.FromResult(loadInstances()));

    Task<InstanceCollection> InitializeAsync(IObservable<WorkspaceState> workspaceStream, CancellationToken cancellationToken);

}

public interface IPartitionedTypeSource: ITypeSource
{
    object GetPartition(object instance);
}


public abstract record TypeSource<TTypeSource> : ITypeSource
    where TTypeSource : TypeSource<TTypeSource>
{
    private readonly IMessageHub hub;

    protected TypeSource(IMessageHub hub, Type ElementType, object DataSource, string CollectionName)
    {
        this.hub = hub;
        this.ElementType = ElementType;
        this.DataSource = DataSource;
        this.CollectionName = CollectionName;
        Key = GetKeyFunction(ElementType);
    }

    ITypeSource ITypeSource.WithKey(Func<object, object> key)
        => This with { Key = key };

    public virtual object GetKey(object instance)
        => Key(instance);

    protected Func<object, object> Key { get; init; }
    private static Func<object, object> GetKeyFunction(Type elementType)
    {
        var keyProperty = elementType?.GetProperties().SingleOrDefault(p => p.HasAttribute<KeyAttribute>());
        if (keyProperty == null)
            keyProperty = elementType?.GetProperties().SingleOrDefault(x => x.Name.ToLowerInvariant() == "id");
        if (keyProperty == null)
            return null;
        var prm = Expression.Parameter(typeof(object));
        return Expression
            .Lambda<Func<object, object>>(Expression.Convert(Expression.Property(Expression.Convert(prm, elementType), keyProperty), typeof(object)), prm)
            .Compile();
    }



    protected TTypeSource This => (TTypeSource)this;

    



    public virtual InstanceCollection Update(WorkspaceState workspace)
    {
        var myCollection = workspace.Reduce(new CollectionReference(CollectionName));

        return UpdateImpl(myCollection);
    }

    private IDisposable workspaceSubscription;


    

    protected virtual InstanceCollection UpdateImpl(InstanceCollection myCollection) => myCollection;


    ITypeSource ITypeSource.WithInitialData(
        Func<CancellationToken, Task<IEnumerable<object>>> initialization)
        => WithInitialData(initialization);

    public TTypeSource WithInitialData(Func<CancellationToken, Task<IEnumerable<object>>> initialization)
        => This with { InitializationFunction = initialization };

    protected Func<CancellationToken, Task<IEnumerable<object>>> InitializationFunction { get; init; }
        = _ => Task.FromResult(Enumerable.Empty<object>());


    public Type ElementType { get; init; }
    public object DataSource { get; init; }
    public string CollectionName { get; init; }



    public virtual async Task<InstanceCollection> InitializeAsync(IObservable<WorkspaceState> workspaceStream, CancellationToken cancellationToken)
    {
        var initialData = await InitializeDataAsync(cancellationToken);
        return new(initialData.ToImmutableDictionary(GetKey, x => x)){GetKey = GetKey};
    }

    private Task<IEnumerable<object>> InitializeDataAsync(CancellationToken cancellationToken) 
        => InitializationFunction(cancellationToken);

    public void Dispose()
    {
        workspaceSubscription?.Dispose();
    }
}

public record PartitionedTypeSourceWithType<T>(IMessageHub Hub, Func<T,object> PartitionFunction, object DataSource) : TypeSourceWithType<T>(Hub, DataSource), IPartitionedTypeSource
{
    public object GetPartition(object instance) => PartitionFunction.Invoke((T)instance);
}

public record TypeSourceWithType<T>(IMessageHub Hub, object DataSource) : TypeSourceWithType<T, TypeSourceWithType<T>>(Hub, DataSource)
{
    protected override InstanceCollection UpdateImpl(InstanceCollection instances)
        => UpdateAction.Invoke(instances);

    protected Func<InstanceCollection, InstanceCollection> UpdateAction { get; init; } = i => i;

    public TypeSourceWithType<T> WithUpdate(Func<InstanceCollection, InstanceCollection> update) => This with { UpdateAction = update };


    public TypeSourceWithType<T> WithInitialData(Func<CancellationToken, Task<IEnumerable<T>>> initialData)
        => WithInitialData(async  c=> (await initialData.Invoke(c)).Cast<object>());

    public TypeSourceWithType<T> WithInitialData(IEnumerable<T> initialData)
        => WithInitialData(_ => Task.FromResult(initialData.Cast<object>()));

}

public abstract record TypeSourceWithType<T, TTypeSource> : TypeSource<TTypeSource>
where TTypeSource: TypeSourceWithType<T, TTypeSource>
{
    protected TypeSourceWithType(IMessageHub hub, object DataSource) : base(hub, typeof(T), DataSource, typeof(T).FullName)
    {
        hub.ServiceProvider.GetRequiredService<ITypeRegistry>().WithType(typeof(T));
    }

    public TTypeSource WithKey(Func<T, object> key)
        => This with { Key = o => key.Invoke((T)o) };

    public TTypeSource WithCollectionName(string collectionName) =>
        This with { CollectionName = collectionName };

    


    public TTypeSource WithQuery(Func<string, T> query)
        => This with { QueryFunction = query };

    protected Func<string, T> QueryFunction { get; init; }

}