using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using OpenSmc.Collections;
using OpenSmc.Reflection;

namespace OpenSmc.Data;

public abstract record TypeSource
{
    public abstract Type ElementType { get; }
    public abstract Task<ImmutableDictionary<object, object>> DoInitialize();
    public abstract object GetKey(object instance);
    internal Func<IEnumerable<object>, Task> DeleteByIds { get; init; }

    public virtual TypeSource Build(DataSource dataSource) => this;

    public abstract TypeSource WithInitialData(IEnumerable<object> initialData);
}

public record TypeSource<T> : TypeSource
{
    public override Type ElementType => typeof(T);

    public override async Task<ImmutableDictionary<object, object>> DoInitialize()
    {
        return (await InitializeAction())
            .Select(x => new KeyValuePair<object,object>(Key(x), x))
            .ToImmutableDictionary();
    }

    public override object GetKey(object instance)
        => Key((T)instance);

    public override TypeSource WithInitialData(IEnumerable<object> initialData)
        => WithInitialData(initialData.Cast<T>());
    public TypeSource<T> WithInitialData(IEnumerable<T> initialData)
        => WithInitialData(() => Task.FromResult<IReadOnlyCollection<T>>(initialData.ToArray()));

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


    internal virtual Task<IReadOnlyCollection<T>> InitializeAsync() => InitializeAction();

    protected Func<Task<IReadOnlyCollection<T>>> InitializeAction { get; init; } =
        () => Task.FromResult<IReadOnlyCollection<T>>(Array.Empty<T>());

    public TypeSource<T> WithInitialData(Func<Task<IReadOnlyCollection<T>>> initialization)
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

    public override TypeSource Build(DataSource dataSource)
    {
        var storage = ((DataSourceWithStorage)dataSource).Storage;
        return this
            with
            {
                Storage = storage,
                AddAction = storage.Add,
                UpdateAction = storage.Update,
                DeleteAction = storage.Delete,
                InitializeAction = async () =>
                {
                    await using var transaction = await storage.StartTransactionAsync();
                    return await storage.Query<T>().ToArrayAsync();
                }
            };
    }

}