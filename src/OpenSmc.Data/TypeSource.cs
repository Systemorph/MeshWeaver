using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using OpenSmc.Reflection;

namespace OpenSmc.Data;

public abstract record TypeSource
{
    public abstract Type ElementType { get; }
    public abstract Task<ImmutableDictionary<object, object>> DoInitialize(CancellationToken cancellationToken);
    public abstract object GetKey(object instance);
    internal Func<IEnumerable<object>, Task> DeleteByIds { get; init; }
    public abstract string CollectionName { get;  } 
    
    public virtual TypeSource Build(IDataSource dataSource) => this;
    
    public abstract TypeSource WithInitialData(IEnumerable<object> initialData);
}

public record TypeSource<T> : TypeSource
{
    public override Type ElementType => typeof(T);

    public override async Task<ImmutableDictionary<object, object>> DoInitialize(CancellationToken cancellationToken)
    {
        return (await InitializeAction(cancellationToken))
            .Select(x => new KeyValuePair<object,object>(Key(x), x))
            .ToImmutableDictionary();
    }

    public override object GetKey(object instance)
        => Key((T)instance);

    public override string CollectionName => CollectionNameImpl;

    private string CollectionNameImpl { get; init; } = typeof(T).FullName;

    public TypeSource<T> WithCollectionName(string collectionName) =>
        this with { CollectionNameImpl = collectionName };

    public override TypeSource WithInitialData(IEnumerable<object> initialData)
        => WithInitialData(initialData.Cast<T>());
    public TypeSource<T> WithInitialData(IEnumerable<T> initialData)
        => WithInitialData(_ => Task.FromResult<IReadOnlyCollection<T>>(initialData.ToArray()));

    protected Func<T, object> Key { get; init; } = GetKeyFunction();
    private static Func<T, object> GetKeyFunction()
    {
        var keyProperty = typeof(T).GetProperties().SingleOrDefault(p => p.HasAttribute<KeyAttribute>());
        if (keyProperty == null)
            keyProperty = typeof(T).GetProperties().SingleOrDefault(x => x.Name.ToLowerInvariant() == "id");
        if (keyProperty == null)
            return null;
        var prm = Expression.Parameter(typeof(T));
        return Expression
            .Lambda<Func<T, object>>(Expression.Convert(Expression.Property(prm, keyProperty), typeof(object)), prm)
            .Compile();
    }

    public TypeSource<T> WithKey(Func<T, object> key)
        => this with { Key = key };


    protected Func<CancellationToken, Task<IReadOnlyCollection<T>>> InitializeAction { get; init; } =
        _ => Task.FromResult<IReadOnlyCollection<T>>(Array.Empty<T>());

    public TypeSource<T> WithInitialData(Func<CancellationToken, Task<IReadOnlyCollection<T>>> initialization)
        => this with { InitializeAction = initialization };

    internal virtual void Update(IReadOnlyCollection<T> instances) => UpdateAction.Invoke(instances);
    protected Action<IReadOnlyCollection<T>> UpdateAction { get; init; } = _ => { };

    public TypeSource<T> WithUpdate(Action<IReadOnlyCollection<T>> update) => this with { UpdateAction = update };
    protected Action<IReadOnlyCollection<T>> AddAction { get; init; } = _ => { };


    internal virtual void Add(IReadOnlyCollection<T> instances) => AddAction.Invoke(instances);

    public TypeSource<T> WithAdd(Action<IReadOnlyCollection<T>> add) => this with { AddAction = add };

    protected Action<IReadOnlyCollection<T>> DeleteAction { get; init; }
    internal virtual void Delete(IReadOnlyCollection<T> instances) => DeleteAction.Invoke(instances);
    public TypeSource<T> WithDelete(Action<IReadOnlyCollection<T>> delete) => this with { DeleteAction = delete };

    public TypeSource<T> WithDeleteById(Func<IEnumerable<object>, Task> delete) => this with { DeleteByIds = delete };
}


public record TypeSourceWithDataStorage<T>
    : TypeSource<T>
    where T : class
{

    private IDataStorage Storage { get; init; }

    public override TypeSource Build(IDataSource dataSource)
    {
        var storage = ((IDataSourceWithStorage)dataSource).Storage;
        return this
            with
            {
                Storage = storage,
                AddAction = storage.Add,
                UpdateAction = storage.Update,
                DeleteAction = storage.Delete,
                InitializeAction = async cancellationToken =>
                {
                    await using var transaction = await storage.StartTransactionAsync(cancellationToken);
                    return await storage.GetData<T>(cancellationToken);
                }
            };
    }

}