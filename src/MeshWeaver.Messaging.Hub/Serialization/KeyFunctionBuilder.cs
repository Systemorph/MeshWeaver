using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using System.Reflection;
using MeshWeaver.Domain;
using MeshWeaver.Reflection;

namespace MeshWeaver.Messaging.Serialization;


public class KeyFunctionBuilder
{
    private readonly List<Func<Type, KeyFunction?>> factories =
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
    ];

    public static KeyFunction? GetFromProperties(
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
        return new(Expression
                .Lambda<Func<object, object>>(
                    Expression.Convert(
                        Expression.New(
                            tupleType.GetConstructors().Single(),
                            properties.Select(
                                x => Expression.Property(Expression.Convert(prm, type), x)
                            )
                        ),
                        typeof(object)
                    ),
                    prm
                )
                .Compile(),
            tupleType
        );
    }

    private static KeyFunction? GetFromProperty(Type type, PropertyInfo? property)
    {
        if (property == null)
            return null;
        var prm = Expression.Parameter(typeof(object));
        return new(
            Expression
            .Lambda<Func<object, object>>(
                Expression.Convert(
                    Expression.Property(Expression.Convert(prm, type), property),
                    typeof(object)
                ),
                prm
            )
            .Compile(),
            property.PropertyType
            );
    }

    public KeyFunction? GetKeyFunction(Type elementType) =>
        factories.Select(f => f(elementType)).FirstOrDefault(f => f != null);

    public void WithKeyFunction(Func<Type, KeyFunction?> factory)
        => factories.Add(factory);

}
