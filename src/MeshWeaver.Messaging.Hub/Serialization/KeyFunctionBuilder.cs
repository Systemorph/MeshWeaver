using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using System.Reflection;
using MeshWeaver.Domain;
using MeshWeaver.Reflection;

namespace MeshWeaver.Messaging.Serialization;


/// <summary>
/// Builds a <see cref="KeyFunction"/> that extracts an entity's identity from a type.
/// Applies an ordered set of factories — a property marked with [Key], then a property
/// named "id", then one named "systemname", then the tuple of the [Dimension] properties — and
/// supports registering additional factories. Used to derive the "$id" key when serializing
/// entities across the mesh.
///
/// <para>A registry's builder chains to its parent registry's: the conventions are the same for every
/// builder, and an additional factory registered on a builder applies to the definitions of that
/// builder and of every builder below it — never above. So a definition the parent built is the one
/// a child would build, unless the child registered factories of its own.</para>
/// </summary>
public class KeyFunctionBuilder
{
    // The single-property CONVENTIONS, in order: a property marked [Key], then one named "id", then one
    // named "systemname" (both case-insensitive). Immutable and identical for every builder.
    private static readonly Func<PropertyInfo, bool>[] Conventions =
    [
        p => p.HasAttribute<KeyAttribute>(),
        p => p.Name.Equals("id", StringComparison.InvariantCultureIgnoreCase),
        p => p.Name.Equals("systemname", StringComparison.InvariantCultureIgnoreCase),
    ];

    // One factory per convention, built once (declared after Conventions: static initializers run
    // in textual order), then the dimension tuple. Immutable; every builder starts from them.
    //
    // The dimension tuple is a convention, not a factory a DataContext adds: it depends on the CLR type
    // alone, so it is the same in every hub. Registered per DataContext it made every data hub's builder
    // differ from its parent's, so no data hub could resolve a definition through its parent.
    private static readonly Func<Type, KeyFunction?>[] ConventionFactories =
    [
        .. Conventions.Select(convention => (Func<Type, KeyFunction?>)(type =>
            GetFromProperty(type, type.GetProperties().SingleOrDefault(convention)))),
        type => GetFromProperties(type, type.GetProperties().Where(p => p.HasAttribute<DimensionAttribute>()).ToArray()),
    ];

    private readonly KeyFunctionBuilder? parent;

    // The factories registered on THIS builder, after the conventions. Replaced, never mutated.
    private ImmutableList<Func<Type, KeyFunction?>> ownFactories = ImmutableList<Func<Type, KeyFunction?>>.Empty;

    /// <summary>Creates a builder with the conventions only.</summary>
    public KeyFunctionBuilder()
    {
    }

    /// <summary>Creates a builder that consults <paramref name="parent"/>'s factories after its own.</summary>
    /// <param name="parent">The parent registry's builder, or null.</param>
    internal KeyFunctionBuilder(KeyFunctionBuilder? parent) => this.parent = parent;

    /// <summary>Whether factories were registered on this builder itself.</summary>
    internal bool HasOwnFactories => !Volatile.Read(ref ownFactories).IsEmpty;

    /// <summary>
    /// Whether the conventions alone decide the key of <paramref name="type"/>: the first single-property
    /// convention any property matches is matched by exactly one property, or no such convention matches
    /// and the type has [Dimension] properties. Then every builder yields the same key function for the
    /// type, whatever factories it was given on top.
    /// </summary>
    /// <param name="type">The CLR type.</param>
    /// <returns>True when the conventions decide the key.</returns>
    internal static bool HasConventionalKey(Type type)
    {
        var properties = type.GetProperties();
        foreach (var convention in Conventions)
        {
            var matches = properties.Count(convention);
            if (matches > 0)
                return matches == 1;
        }
        return properties.Any(p => p.HasAttribute<DimensionAttribute>());
    }

    /// <summary>The key from the factories registered on this builder itself, ignoring the parent's.</summary>
    /// <param name="type">The CLR type.</param>
    /// <returns>The key function, or null.</returns>
    internal KeyFunction? GetOwnFactoryKey(Type type) =>
        Volatile.Read(ref ownFactories).Select(f => f(type)).FirstOrDefault(f => f != null);

    private KeyFunction? GetFactoryKey(Type type) => GetOwnFactoryKey(type) ?? parent?.GetFactoryKey(type);

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
        ConventionFactories.Select(f => f(elementType)).FirstOrDefault(f => f != null)
        ?? GetFactoryKey(elementType);

    /// <summary>
    /// Registers an additional key-function factory, appended after the built-in factories
    /// so it acts as a further fallback.
    /// </summary>
    /// <param name="factory">A factory mapping a type to a <see cref="KeyFunction"/> (or <c>null</c> if it does not apply).</param>
    public void WithKeyFunction(Func<Type, KeyFunction?> factory)
        => ImmutableInterlocked.Update(ref ownFactories, list => list.Add(factory));

}
