using System.Collections.Immutable;
using System.Reflection;
using OpenSmc.Messaging;
using OpenSmc.Reflection;

namespace OpenSmc.Data;

public interface IDataSource
{
    bool GetTypeConfiguration(Type type, out TypeSource typeConfiguration);
    IEnumerable<Type> MappedTypes { get; }
    object Id { get; }
    internal Task<WorkspaceState> InitializeAsync(IMessageHub hub, CancellationToken cancellationToken);
    internal Task<ITransaction> StartTransactionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Idea is to split the construction of the configuration in two parts:
    ///
    /// 1. Fluent builder to configure types, mappings, db settings, etc
    /// 2. Build step where configuration is finished. This can be used to build up services, etc.
    /// </summary>
    /// <returns></returns>
    IDataSource Build(IMessageHub hub);
}
public record DataSource<TDataSource>(object Id) : IDataSource
where TDataSource : DataSource<TDataSource>
{
    public TDataSource WithType(Type type)
        => WithType(type, x => x);

    public TDataSource WithType(Type type, Func<TypeSource, TypeSource> config)
        => (TDataSource)WithTypeMethod.MakeGenericMethod(type).InvokeAsFunction(this, config);

    private static readonly MethodInfo WithTypeMethod =
        ReflectionHelper.GetMethodGeneric<DataSource<TDataSource>>(x => x.WithType<object>(default(Func<TypeSource, TypeSource>)));
    public TDataSource WithType<T>()
        where T : class
        => WithType<T>(d => d);

    // ReSharper disable once UnusedMethodReturnValue.Local
    private TDataSource WithType<T>(
        Func<TypeSource, TypeSource> configurator)
        where T : class
        => WithType<T>(x => (TypeSource<T>)configurator.Invoke(x));

    public TDataSource WithType<T>(
        Func<TypeSource<T>, TypeSource<T>> configurator)
        where T : class
        => WithType(configurator.Invoke(CreateTypeSource<T>()));

    protected virtual TypeSource<T> CreateTypeSource<T>() where T : class
    {
        return new TypeSource<T>();
    }

    protected TDataSource WithType<T>(TypeSource<T> typeSource)
        where T : class
    {
        return (TDataSource)this with
        {
            TypeSources = TypeSources.SetItem(typeof(T), typeSource)
        };
    }

    async Task<WorkspaceState> IDataSource.InitializeAsync(IMessageHub hub, CancellationToken cancellationToken)
    {
        return await InitializeAsync(hub, cancellationToken);
    }

    protected virtual async Task<WorkspaceState> InitializeAsync(IMessageHub hub, CancellationToken cancellationToken)
    {
        var ret = new WorkspaceState(this);

        foreach (var typeConfiguration in TypeSources.Values)
        {
            var initialized = await typeConfiguration.DoInitialize(cancellationToken);
            ret = ret.SetData(typeConfiguration.ElementType, initialized);
        }

        return ret;
    }


    protected ImmutableDictionary<Type, TypeSource> TypeSources { get; init; } = ImmutableDictionary<Type, TypeSource>.Empty;


    public TDataSource WithTransaction(Func<CancellationToken, Task<ITransaction>> startTransaction)
        => (TDataSource)this with { StartTransactionAction = startTransaction };


    Task<ITransaction> IDataSource.StartTransactionAsync(CancellationToken cancellationToken) => StartTransactionAction(cancellationToken);
    internal Func<CancellationToken, Task<ITransaction>> StartTransactionAction { get; init; }
        = _ => Task.FromResult<ITransaction>(EmptyTransaction.Instance);

    public IEnumerable<Type> MappedTypes => TypeSources.Keys;

    public TypeSource GetTypeSource(string collectionName) =>
        TypeSources.Values.FirstOrDefault(x => x.CollectionName == collectionName);

    public bool GetTypeConfiguration(Type type, out TypeSource typeSource)
    {
        return TypeSources.TryGetValue(type, out typeSource);
    }


    /// <summary>
    /// Idea is to split the construction of the configuration in two parts:
    ///
    /// 1. Fluent builder to configure types, mappings, db settings, etc
    /// 2. Build step where configuration is finished. This can be used to build up services, etc.
    /// </summary>
    /// <returns></returns>

    IDataSource IDataSource.Build(IMessageHub hub) => Build(hub);
    protected virtual IDataSource Build(IMessageHub hub)
    {
        var builtUp = Buildup(hub);
        return builtUp with
        {
            TypeSources = TypeSources
                .ToImmutableDictionary
                (
                    x => x.Key,
                    // here we build up the elements
                    x => x.Value.Build(builtUp)
                )
        };
    }

    protected virtual TDataSource Buildup(IMessageHub hub)
        => (TDataSource)this;
}

public record DataSource(object Id) : DataSource<DataSource>(Id);