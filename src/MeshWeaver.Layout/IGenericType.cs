namespace MeshWeaver.Layout;

/// <summary>
/// Represents a generic type with base type and type arguments.
/// </summary>
public interface IGenericType
{
    /// <summary>
    /// Gets the base type of the generic type.
    /// </summary>
    Type BaseType { get; }

    /// <summary>
    /// Gets the type arguments of the generic type.
    /// </summary>
    Type[] TypeArguments { get; }
}
