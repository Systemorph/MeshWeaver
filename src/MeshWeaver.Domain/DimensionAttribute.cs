#nullable enable
namespace MeshWeaver.Domain;

/// <summary>
/// Declares that the annotated field or property references a dimension of the given type,
/// optionally under a custom dimension name.
/// </summary>
/// <param name="type">The CLR type of the dimension this member maps to.</param>
/// <param name="name">An optional dimension name; defaults to the type's name when omitted.</param>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public class DimensionAttribute(Type type, string? name = null) : Attribute
{
    /// <summary>
    /// The dimension name; falls back to the dimension type's name when not specified.
    /// </summary>
    public string Name { get; } = name ?? type.Name;
    /// <summary>
    /// The CLR type of the referenced dimension.
    /// </summary>
    public Type Type { get; } = type;
    /// <summary>
    /// Optional additional options associated with the dimension reference.
    /// </summary>
    public object? Options;
}

/// <summary>
/// Strongly typed variant of <see cref="DimensionAttribute"/> for a dimension of type <typeparamref name="T"/>.
/// </summary>
/// <typeparam name="T">The CLR type of the referenced dimension.</typeparam>
/// <param name="name">An optional dimension name; defaults to the type's name when omitted.</param>
public class DimensionAttribute<T>(string? name = null) : DimensionAttribute(typeof(T), name);
