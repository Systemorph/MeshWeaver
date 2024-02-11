using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using OpenSmc.Collections;
using OpenSmc.DataSource.Abstractions;
using OpenSmc.Reflection;

namespace OpenSmc.Data;

public abstract record TypeSource
{
    public abstract Type ElementType { get; }
    public abstract Task<ImmutableDictionary<object, object>> DoInitialize();
    public abstract object GetKey(object instance);
    internal Func<IEnumerable<object>, Task> DeleteByIds { get; init; }

    public virtual TypeSource Build(DataSource dataSource) => this;
}

public record TypeSource<T> : TypeSource
{
    public override Type ElementType => typeof(T);

    public override async Task<ImmutableDictionary<object, object>> DoInitialize()
    {
        return (await Initialize())
            .Select(x => new KeyValuePair<object,object>(Key(x), x))
            .ToImmutableDictionary();
    }

    public override object GetKey(object instance)
        => Key((T)instance);

    internal Func<T, object> Key { get; init; } = GetKeyFunction();
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
    internal Func<Task<IReadOnlyCollection<T>>> Initialize { get; init; }
    public TypeSource<T> WithInitialization(Func<Task<IReadOnlyCollection<T>>> initialization)
        => this with { Initialize = initialization };
    internal Action<IReadOnlyCollection<T>> Update { get; init; }

    public TypeSource<T> WithUpdate(Action<IReadOnlyCollection<T>> update) => this with { Update = update };
    internal Action<IReadOnlyCollection<T>> Add { get; init; }

    public TypeSource<T> WithAdd(Action<IReadOnlyCollection<T>> add) => this with { Add = add };

    internal Action<IReadOnlyCollection<T>> Delete { get; init; }
    public TypeSource<T> WithDelete(Action<IReadOnlyCollection<T>> delete) => this with { Delete = delete };

    public TypeSource<T> WithDeleteById(Func<IEnumerable<object>, Task> delete) => this with { DeleteByIds = delete };
}


public record TypeSourceWithDataStorage<T>
    : TypeSource<T>
    where T : class
{
    public TypeSourceWithDataStorage()
    {
        Add = o => Storage!.Add(o);
        Delete = o => Storage!.Add(o);
        Update = o => Storage!.Add(o);
        Initialize = async () =>
        {
            await using var transaction = await Storage!.StartTransactionAsync();
            return await Storage.Query<T>().ToArrayAsync();
        };

    }
    protected IDataStorage Storage { get; private set; }

    public override TypeSource Build(DataSource dataSource)
    {
        Storage = ((DataSourceWithStorage)dataSource).Storage;
        return this;
    }

}