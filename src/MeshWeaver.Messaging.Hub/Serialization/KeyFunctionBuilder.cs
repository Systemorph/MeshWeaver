using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using System.Reflection;
using MeshWeaver.Domain;
using MeshWeaver.Reflection;

namespace MeshWeaver.Messaging.Serialization;


/// <summary>
/// Builds a <see cref="KeyFunction"/> that extracts an entity's identity from a type.
/// Applies an ordered set of factories — a property marked with [Key], then a property
/// named "id", then one named "systemname" — and supports registering additional
/// factories. Used to derive the "$id" key when serializing entities across the mesh.
/// </summary>
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

    /// <summary>
    /// Builds a <see cref="KeyFunction"/> whose key is a tuple of the supplied properties
    /// (one to eight), compiling an expression that projects an instance onto that tuple.
    /// </summary>
    /// <param name="type">The entity type the properties belong to.</param>
    /// <param name="properties">The properties that together form the key, in order.</param>
    /// <returns>
    /// A key function producing the tuple key, or <c>null</c> when no properties are supplied.
    /// </returns>
    /// <exception cref="NotSupportedException">Thrown when more than eight properties are supplied.</exception>
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

    /// <summary>
    /// Resolves the key function for a type by applying the registered factories in order
    /// and returning the first non-null result.
    /// </summary>
    /// <param name="elementType">The type whose key function is requested.</param>
    /// <returns>The first matching <see cref="KeyFunction"/>, or <c>null</c> if none applies.</returns>
    public KeyFunction? GetKeyFunction(Type elementType) =>
        factories.Select(f => f(elementType)).FirstOrDefault(f => f != null);

    /// <summary>
    /// Registers an additional key-function factory, appended after the built-in factories
    /// so it acts as a further fallback.
    /// </summary>
    /// <param name="factory">A factory mapping a type to a <see cref="KeyFunction"/> (or <c>null</c> if it does not apply).</param>
    public void WithKeyFunction(Func<Type, KeyFunction?> factory)
        => factories.Add(factory);

}
