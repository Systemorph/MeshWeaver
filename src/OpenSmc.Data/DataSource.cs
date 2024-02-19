using System.Collections.Immutable;
using System.Reflection;
using OpenSmc.Reflection;

namespace OpenSmc.Data;

public record DataSource(object Id)
{
    public DataSource WithType(Type type)
        => WithType(type, x => x);
    public DataSource WithType(Type type, Func<TypeSource, TypeSource> config)
    => (DataSource)WithTypeMethod.MakeGenericMethod(type).InvokeAsFunction(this, config);

    private static readonly MethodInfo WithTypeMethod =
        ReflectionHelper.GetMethodGeneric<DataSource>(x => x.WithType<object>(default(Func<TypeSource,TypeSource>)));
    public DataSource WithType<T>()
        where T : class
        => WithType<T>(d => d);

    // ReSharper disable once UnusedMethodReturnValue.Local
    private DataSource WithType<T>(
        Func<TypeSource, TypeSource> configurator)
        where T : class
        => WithType<T>(x => (TypeSource<T>)configurator.Invoke(x));

    public DataSource WithType<T>(
        Func<TypeSource<T>, TypeSource<T>> configurator)
        where T : class
        => WithType(configurator.Invoke(new TypeSource<T>()));


    public async Task<WorkspaceState> DoInitialize(CancellationToken cancellationToken)
    {
        var ret = new WorkspaceState(this);

        foreach (var typeConfiguration in TypeSources.Values)
        {
            var initialized = await typeConfiguration.DoInitialize(cancellationToken);
            ret = ret.SetData(typeConfiguration.ElementType, initialized);
        }

        return ret;
    }
    protected DataSource WithType<T>(TypeSource<T> typeSource)
        where T : class
    {
        return this with
        {
            TypeSources = TypeSources.SetItem(typeof(T), typeSource)
        };
    }


    protected ImmutableDictionary<Type, TypeSource> TypeSources { get; init; } = ImmutableDictionary<Type, TypeSource>.Empty;


    public DataSource WithTransaction(Func<CancellationToken, Task<ITransaction>> startTransaction)
        => this with { StartTransactionAction = startTransaction };


    internal Task<ITransaction> StartTransactionAsync(CancellationToken cancellationToken) => StartTransactionAction(cancellationToken);
    internal Func<CancellationToken, Task<ITransaction>> StartTransactionAction { get; init; }
        = _ => Task.FromResult<ITransaction>(EmptyTransaction.Instance);

    public IEnumerable<Type> MappedTypes => TypeSources.Keys;

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
    internal virtual DataSource Build()
    {
        var builtUp = Buildup();
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

    protected virtual DataSource Buildup()
        => this;
}