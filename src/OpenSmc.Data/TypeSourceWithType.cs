using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Data.Persistence;
using OpenSmc.Messaging;
using OpenSmc.Reflection;
using OpenSmc.Serialization;

namespace OpenSmc.Data;

public interface ITypeSource
{
    Task InitializeAsync(CancellationToken cancellationToken);
    void Initialize(IEnumerable<EntityDescriptor> entities);
    Type ElementType { get; }
    string CollectionName { get; }
    object GetKey(object instance);
    ITypeSource WithKey(Func<object, object> key);
    IReadOnlyCollection<EntityDescriptor> GetData();
    IReadOnlyCollection<DataChangeRequest> RequestChange(DataChangeRequest request);
    ITypeSource WithPartition<T>(Func<T, object> partition);
    ITypeSource WithPartition(Type type, Func<object, object> partition);
    object GetData(object id);
    IEnumerable<DataChangeRequest> Update(IReadOnlyCollection<EntityDescriptor> entities, bool snapshot = false);

    object GetPartition(object instance);
}

public abstract record TypeSource<TTypeSource>(Type ElementType, object DataSource, string CollectionName, IMessageHub Hub) : ITypeSource
    where TTypeSource : TypeSource<TTypeSource>
{

    protected readonly ISerializationService SerializationService = Hub.ServiceProvider.GetRequiredService<ISerializationService>();
    protected ImmutableDictionary<object, object> CurrentState { get; set; }


    ITypeSource ITypeSource.WithKey(Func<object, object> key)
        => This with { Key = key };

    public IReadOnlyCollection<EntityDescriptor> GetData()
    {
        return CurrentState.Select(x => new EntityDescriptor(CollectionName, x.Key, x.Value)).ToArray();
    }

    public virtual object GetKey(object instance)
        => Key(instance);
    protected Func<object, object> Key { get; init; } = GetKeyFunction(ElementType);
    private static Func<object, object> GetKeyFunction(Type elementType)
    {
        var keyProperty = elementType.GetProperties().SingleOrDefault(p => p.HasAttribute<KeyAttribute>());
        if (keyProperty == null)
            keyProperty = elementType.GetProperties().SingleOrDefault(x => x.Name.ToLowerInvariant() == "id");
        if (keyProperty == null)
            return null;
        var prm = Expression.Parameter(typeof(object));
        return Expression
            .Lambda<Func<object, object>>(Expression.Convert(Expression.Property(Expression.Convert(prm, elementType), keyProperty), typeof(object)), prm)
            .Compile();
    }


    
    protected TTypeSource This => (TTypeSource)this;






    public virtual Task<IReadOnlyCollection<EntityDescriptor>> GetAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyCollection<EntityDescriptor>>(null);
    }


    public IReadOnlyCollection<DataChangeRequest> RequestChange(DataChangeRequest request)
    {
        switch (request)
        {
            case UpdateDataRequest update:
                return Update(update.Elements.Select(ParseEntityDescriptor).ToArray()).ToArray();

            case DeleteDataRequest delete:
                return Delete(delete.Elements.Select(ParseEntityDescriptor).ToArray()).ToArray();

        }
        throw new ArgumentOutOfRangeException(nameof(request), request, null);
    }



    protected Func<object, object> PartitionFunction { get; init; } = _ => DataSource;
    public ITypeSource WithPartition<T>(Func<T, object> partition)
        => WithPartition(typeof(T), o => partition.Invoke((T)o));

    public ITypeSource WithPartition(Type type, Func<object, object> partition) 
        => this with { PartitionFunction = partition };

    protected EntityDescriptor ParseEntityDescriptor(object instance)
    {
        var id = GetKey(instance);
        return new(CollectionName, id, instance);
    }


    public object GetData(object id)
    {
        return CurrentState.GetValueOrDefault(id);
    }



    protected IEnumerable<DataChangeRequest> Delete(IReadOnlyCollection<EntityDescriptor> entities)
    {
            CurrentState = CurrentState.RemoveRange(entities.Select(a => a.Id));
            var toBeDeleted = entities.Select(a => a.Entity).ToArray();
            DeleteImpl(toBeDeleted);
            yield return new DeleteDataRequest(toBeDeleted);
    }



    public IEnumerable<DataChangeRequest> Update(IReadOnlyCollection<EntityDescriptor> entities, bool snapshot = false)
    {
        var toBeUpdated = entities
            .Where(e => CurrentState.TryGetValue(e.Id, out var existing) && !existing.Equals(e.Entity))
            .Select(e => e)
            .ToArray();

        if (toBeUpdated.Any())
        {
            CurrentState =
                CurrentState.SetItems(toBeUpdated.Select(x => new KeyValuePair<object, object>(x.Id, x.Entity)));
            UpdateImpl(toBeUpdated);
            yield return new UpdateDataRequest(toBeUpdated.Select(x => x.Entity).ToArray());
        }

        var toBeDeleted = snapshot
            ? CurrentState.RemoveRange(entities.Select(x => x.Id))
            : ImmutableDictionary<object, object>.Empty;

        if (toBeDeleted.Any())
        {
            CurrentState = CurrentState.RemoveRange(toBeDeleted.Keys);
            yield return new DeleteDataRequest(toBeDeleted.Select(x => x.Value).ToArray());
        }


    }


    public object GetPartition(object instance)
        => PartitionFunction.Invoke(instance);

    protected abstract void UpdateImpl(IEnumerable<object> instances);
    protected abstract void DeleteImpl(IEnumerable<object> instances);


    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Initialize(await GetAsync(cancellationToken));
    }

    public void Initialize(IEnumerable<EntityDescriptor> entities)
    {
        CurrentState = entities
            .ToImmutableDictionary(x => x.Id, x => x.Entity);
    }
}

public record TypeSourceWithType<T>(object DataSource, IMessageHub Hub) : TypeSourceWithType<T, TypeSourceWithType<T>>(DataSource, Hub)
{
    protected override void UpdateImpl(IEnumerable<T> instances) => UpdateAction.Invoke(instances);
    protected Action<IEnumerable<T>> UpdateAction { get; init; } = _ => { };

    public TypeSourceWithType<T> WithUpdate(Action<IEnumerable<T>> update) => This with { UpdateAction = update };
    protected Action<IEnumerable<T>> AddAction { get; init; } = _ => { };


    protected virtual void AddImpl(IEnumerable<T> instances) => AddAction.Invoke(instances);

    public TypeSourceWithType<T> WithAdd(Action<IEnumerable<T>> add) => This with { AddAction = add };

    protected Action<IEnumerable<T>> DeleteAction { get; init; } = _ => { };
    protected override void DeleteImpl(IEnumerable<T> instances) => DeleteAction.Invoke(instances);


    public TypeSourceWithType<T> WithDelete(Action<IEnumerable<T>> delete) => This with { DeleteAction = delete };


    protected Func<CancellationToken, Task<IReadOnlyCollection<EntityDescriptor>>> GetAction { get; init; }
    public TypeSourceWithType<T> WithGet(Func<CancellationToken, Task<IReadOnlyCollection<EntityDescriptor>>> getAction) => This with { GetAction = getAction };

    public override Task<IReadOnlyCollection<EntityDescriptor>> GetAsync(CancellationToken cancellationToken)
        => GetAction?.Invoke(cancellationToken) ??
           Task.FromResult<IReadOnlyCollection<EntityDescriptor>>(Array.Empty<EntityDescriptor>());
    public virtual TypeSourceWithType<T> WithInitialData(Func<CancellationToken,Task<IEnumerable<T>>> initialData)
    {
        return this with
        {
            GetAction = async c => (await initialData(c)).Select(el => ParseEntityDescriptor(el)).ToArray()
        };
    }

}

public abstract record TypeSourceWithType<T, TTypeSource> : TypeSource<TTypeSource>
where TTypeSource: TypeSourceWithType<T, TTypeSource>
{
    protected TypeSourceWithType(object DataSource, IMessageHub Hub) : base(typeof(T), DataSource, typeof(T).FullName, Hub)
    {
        Hub.ServiceProvider.GetRequiredService<ITypeRegistry>().WithType(typeof(T));
    }

    public TTypeSource WithKey(Func<T, object> key)
        => This with { Key = o => key.Invoke((T)o) };

    public TTypeSource WithCollectionName(string collectionName) =>
        This with { CollectionName = collectionName };


    protected override void UpdateImpl(IEnumerable<object> instances)
    => UpdateImpl(instances.Cast<T>());

    protected abstract void UpdateImpl(IEnumerable<T> instances);

    protected override void DeleteImpl(IEnumerable<object> instances)
        => DeleteImpl(instances.Cast<T>());


    protected abstract void DeleteImpl(IEnumerable<T> instances);


    
    public TTypeSource WithPartition(Func<T, object> partition)
        => This with { PartitionFunction = o => partition.Invoke((T)o) };


    public void Deconstruct(out object DataSource, out IMessageHub Hub)
    {
        DataSource = this.DataSource;
        Hub = this.Hub;
    }
}