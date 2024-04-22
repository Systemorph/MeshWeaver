using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Data.Serialization;
using OpenSmc.Domain;
using OpenSmc.Messaging;
using OpenSmc.Messaging.Serialization;
using OpenSmc.Reflection;

namespace OpenSmc.Data;

public static class KeyFunctionBuilder
{
    private static readonly Func<Type, Func<object, object>>[] factories =
    [
        type =>
            GetFromProperty(
                type,
                type.GetProperties().SingleOrDefault(p => p.HasAttribute<KeyAttribute>())
            ),
        type =>
            GetFromProperty(
                type,
                type.GetProperties()
                    .SingleOrDefault(x =>
                        x.Name.Equals("id", StringComparison.InvariantCultureIgnoreCase)
                    )
            ),
        type =>
            GetFromProperty(
                type,
                type.GetProperties()
                    .SingleOrDefault(x =>
                        x.Name.Equals("systemname", StringComparison.InvariantCultureIgnoreCase)
                    )
            ),
        type =>
            GetFromProperties(
                type,
                type.GetProperties().Where(x => x.HasAttribute<DimensionAttribute>()).ToArray()
            ),
    ];

    private static Func<object, object> GetFromProperties(
        Type type,
        IReadOnlyCollection<PropertyInfo> properties
    )
    {
        if (properties.Count == 0)
            return null;

        var propertyTypes = properties.Select(x => x.PropertyType).ToArray();
        var tupleType = properties.Count switch
        {
            1 => typeof(Tuple<>).MakeGenericType(propertyTypes),
            2 => typeof(Tuple<,>).MakeGenericType(propertyTypes),
            3 => typeof(Tuple<,,>).MakeGenericType(propertyTypes),
            4 => typeof(Tuple<,,,>).MakeGenericType(propertyTypes),
            5 => typeof(Tuple<,,,,>).MakeGenericType(propertyTypes),
            6 => typeof(Tuple<,,,,,>).MakeGenericType(propertyTypes),
            7 => typeof(Tuple<,,,,,,>).MakeGenericType(propertyTypes),
            8 => typeof(Tuple<,,,,,,,>).MakeGenericType(propertyTypes),
            _ => throw new NotSupportedException("Too many properties")
        };

        var prm = Expression.Parameter(typeof(object));
        return Expression
            .Lambda<Func<object, object>>(
                Expression.Convert(
                    Expression.New(
                        tupleType.GetConstructors().Single(),
                        properties.Select(
                            (x, i) => Expression.Property(Expression.Convert(prm, type), x)
                        )
                    ),
                    typeof(object)
                ),
                prm
            )
            .Compile();
    }

    private static Func<object, object> GetFromProperty(Type type, PropertyInfo property)
    {
        if (property == null)
            return null;
        var prm = Expression.Parameter(typeof(object));
        return Expression
            .Lambda<Func<object, object>>(
                Expression.Convert(
                    Expression.Property(Expression.Convert(prm, type), property),
                    typeof(object)
                ),
                prm
            )
            .Compile();
    }

    public static Func<object, object> GetKeyFunction(Type elementType) =>
        factories.Select(f => f(elementType)).FirstOrDefault(f => f != null);
}

public abstract record TypeSource<TTypeSource> : ITypeSource
    where TTypeSource : TypeSource<TTypeSource>
{
    protected TypeSource(IMessageHub hub, Type ElementType, object DataSource)
    {
        this.ElementType = ElementType;
        this.DataSource = DataSource;
        var typeRegistry = hub
            .ServiceProvider.GetRequiredService<ITypeRegistry>()
            .WithType(ElementType);
        CollectionName = typeRegistry.TryGetTypeName(ElementType, out var typeName)
            ? typeName
            : ElementType.FullName;
        Key = KeyFunctionBuilder.GetKeyFunction(ElementType);
    }

    ITypeSource ITypeSource.WithKey(Func<object, object> key) => This with { Key = key };

    public virtual object GetKey(object instance) => Key(instance);

    protected Func<object, object> Key { get; init; }

    protected TTypeSource This => (TTypeSource)this;

    public virtual InstanceCollection Update(ChangeItem<WorkspaceState> workspace)
    {
        var myCollection = workspace.Value.Reduce(new CollectionReference(CollectionName));

        return UpdateImpl(myCollection);
    }

    private IDisposable workspaceSubscription;

    protected virtual InstanceCollection UpdateImpl(InstanceCollection myCollection) =>
        myCollection;

    ITypeSource ITypeSource.WithInitialData(
        Func<CancellationToken, Task<IEnumerable<object>>> initialization
    ) => WithInitialData(initialization);

    public TTypeSource WithInitialData(
        Func<CancellationToken, Task<IEnumerable<object>>> initialization
    ) => This with { InitializationFunction = initialization };

    protected Func<
        CancellationToken,
        Task<IEnumerable<object>>
    > InitializationFunction { get; init; } = _ => Task.FromResult(Enumerable.Empty<object>());

    public Type ElementType { get; init; }
    public object DataSource { get; init; }
    public string CollectionName { get; init; }

    public virtual async Task<InstanceCollection> InitializeAsync(
        CancellationToken cancellationToken
    )
    {
        var initialData = await InitializeDataAsync(cancellationToken);
        return new()
        {
            Instances = initialData.ToImmutableDictionary(GetKey, x => x),
            GetKey = GetKey
        };
    }

    private Task<IEnumerable<object>> InitializeDataAsync(CancellationToken cancellationToken) =>
        InitializationFunction(cancellationToken);

    public void Dispose()
    {
        workspaceSubscription?.Dispose();
    }
}
