
using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using System.Security.Cryptography.X509Certificates;
using OpenSmc.Collections;
using OpenSmc.DataSource.Abstractions;
using OpenSmc.Reflection;

namespace OpenSmc.Data;

public abstract record TypeConfiguration
{
    public DataSource DataSource { get; init; }

    public abstract Type ElementType { get; }
    public abstract Task<ImmutableDictionary<object, object>> DoInitialize();
    public abstract object GetKey(object instance);
    internal Func<IEnumerable<object>, Task> DeleteByIds { get; init; }

    public virtual TypeConfiguration Initialize(DataSource dataSource)
        => this with { DataSource = dataSource };
}

public record TypeConfiguration<T> : TypeConfiguration
{
    public override Type ElementType => typeof(T);

    public override async Task<ImmutableDictionary<object, object>> DoInitialize()
    {
        return (await Initialization())
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

    public TypeConfiguration<T> WithKey(Func<T, object> key)
        => this with { Key = key };
    internal Func<Task<IReadOnlyCollection<T>>> Initialization { get; init; }
    public TypeConfiguration<T> WithInitialization(Func<Task<IReadOnlyCollection<T>>> initialization)
        => this with { Initialization = initialization };
    internal Action<IReadOnlyCollection<T>> Update { get; init; }

    public TypeConfiguration<T> WithUpdate(Action<IReadOnlyCollection<T>> update) => this with { Update = update };
    internal Action<IReadOnlyCollection<T>> Add { get; init; }

    public TypeConfiguration<T> WithAdd(Action<IReadOnlyCollection<T>> add) => this with { Add = add };

    internal Action<IReadOnlyCollection<T>> Delete { get; init; }
    public TypeConfiguration<T> WithDelete(Action<IReadOnlyCollection<T>> delete) => this with { Delete = delete };

    public TypeConfiguration<T> WithDeleteById(Func<IEnumerable<object>, Task> delete) => this with { DeleteByIds = delete };
}


public record TypeConfigurationWithDataStorage<T> : TypeConfiguration<T> where T : class
{
    private readonly Func<DataSource, IDataStorage> storageFactory;
    public IDataStorage Storage { get; init; }

    public TypeConfigurationWithDataStorage(Func<DataSource, IDataStorage> storageFactory)
    {
        this.storageFactory = storageFactory;
    }

    public override TypeConfiguration Initialize(DataSource dataSource)
    {
        var storage = storageFactory.Invoke(dataSource);
        return this
            with
            {
                DataSource = dataSource,
                Storage = storage,
                Delete = o => storage.Delete(o),
                Add = o => storage.Add(o),
                Update = o => storage.Update(o),
                Initialization = async () =>
                {
                    await using var transaction = await storage.StartTransactionAsync();
                    return await storage.Query<T>().ToArrayAsync();
                }
            };

    }
}