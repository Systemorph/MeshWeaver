
namespace MeshWeaver.Domain;

/// <summary>
/// Describes a registered CLR type: its collection name, key extraction, and display metadata.
/// </summary>
public interface ITypeDefinition
{
    /// <summary>
    /// The CLR type being described.
    /// </summary>
    Type Type { get; }
    /// <summary>
    /// The human-readable name used to display the type.
    /// </summary>
    string DisplayName { get; }
    /// <summary>
    /// The collection (logical store) name under which instances of the type are addressed.
    /// </summary>
    string CollectionName { get; }
    /// <summary>
    /// An optional icon associated with the type, or <c>null</c> if none.
    /// </summary>
    object? Icon { get; }
    /// <summary>
    /// Extracts the identity key for the given instance of the type.
    /// </summary>
    /// <param name="instance">The instance whose key should be returned.</param>
    /// <returns>The key value identifying the instance.</returns>
    object GetKey(object instance);
    /// <summary>
    /// An optional sort position for the type, or <c>null</c> if unspecified.
    /// </summary>
    int? Order { get; }
    /// <summary>
    /// An optional grouping name used to cluster related types, or <c>null</c> if none.
    /// </summary>
    string? GroupName { get; }
    /// <summary>
    /// A human-readable description of the type.
    /// </summary>
    string Description { get; }
    /// <summary>
    /// Returns the CLR type of this type's identity key.
    /// </summary>
    /// <returns>The type of the key returned by <see cref="GetKey"/>.</returns>
    Type GetKeyType();

}



