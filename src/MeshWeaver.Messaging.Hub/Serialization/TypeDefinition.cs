using System.ComponentModel.DataAnnotations;
using System.Reflection;
using MeshWeaver.Domain;
using MeshWeaver.Utils;
using Namotion.Reflection;

namespace MeshWeaver.Messaging.Serialization;

/// <summary>
/// Describes a CLR type known to the mesh: its serialization collection name, display metadata
/// (name, group, order, icon, description) derived from attributes/XML docs, optional owning address,
/// and the key function used to identify instances of the type.
/// </summary>
public record TypeDefinition : ITypeDefinition
{
    /// <summary>
    /// Initializes a type definition, deriving display name, group, order, icon and description from the
    /// type's <see cref="DisplayAttribute"/>, icon attribute and XML doc summary.
    /// </summary>
    /// <param name="elementType">The CLR type being described.</param>
    /// <param name="typeName">The collection / serialization name for the type.</param>
    /// <param name="keyFunctionBuilder">Builder that resolves the key function for instances of the type.</param>
    public TypeDefinition(Type elementType, string typeName, KeyFunctionBuilder keyFunctionBuilder)
    {
        Type = elementType;
        CollectionName = typeName;

        var displayAttribute = Type.GetCustomAttribute<DisplayAttribute>();
        DisplayName = displayAttribute?.GetName() ?? Type.Name.Wordify();

        GroupName = displayAttribute?.GetGroupName();
        Order = displayAttribute?.GetOrder();
        var iconAttribute = Type.GetCustomAttribute<IconAttribute>();
        if (iconAttribute != null)
            Icon = new Icon(iconAttribute.Provider, iconAttribute.Id);

        Key = new(() => keyFunctionBuilder.GetKeyFunction(Type)!);
        
        Description = Type.GetXmlDocsSummary();
    }

    /// <summary>
    /// Initializes a type definition as above and additionally associates it with an owning address.
    /// </summary>
    /// <param name="elementType">The CLR type being described.</param>
    /// <param name="typeName">The collection / serialization name for the type.</param>
    /// <param name="keyFunctionBuilder">Builder that resolves the key function for instances of the type.</param>
    /// <param name="address">The address that owns instances of this type.</param>
    public TypeDefinition(Type elementType, string typeName, KeyFunctionBuilder keyFunctionBuilder, Address address)
        : this(elementType, typeName, keyFunctionBuilder)
    {
        Address = address;
    }


    /// <summary>The CLR type being described.</summary>
    public Type Type { get; }
    /// <summary>The human-readable display name, from <see cref="DisplayAttribute"/> or the wordified type name.</summary>
    public string DisplayName { get; }
    /// <summary>The collection / serialization name used to identify this type in the mesh.</summary>
    public string CollectionName { get; }
    /// <summary>The icon associated with the type, if an icon attribute is present; otherwise <c>null</c>.</summary>
    public object? Icon { get; init; }
    /// <summary>The address that owns instances of this type, if any.</summary>
    public Address? Address { get; init; }

    /// <summary>The display ordering hint, from <see cref="DisplayAttribute"/>, if specified.</summary>
    public int? Order { get; }
    /// <summary>The display group name, from <see cref="DisplayAttribute"/>, if specified.</summary>
    public string? GroupName { get; }
    /// <summary>The description, taken from the type's XML documentation summary.</summary>
    public string Description { get; init; }

    /// <summary>
    /// Returns the key identifying the given instance using the type's configured key function.
    /// </summary>
    /// <param name="instance">The instance to extract the key from.</param>
    /// <returns>The key value for the instance.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no key mapping is defined for the type.</exception>
    public virtual object GetKey(object instance) =>
        Key.Value.Function(instance)
        ?? throw new InvalidOperationException(
            $"No key mapping is defined for type {CollectionName}. Please specify in the configuration of the data sources source.");

    /// <summary>
    /// Returns the CLR type of the key produced by the type's configured key function.
    /// </summary>
    /// <returns>The key's CLR type.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no key mapping is defined for the type.</exception>
    public Type GetKeyType() =>
        Key.Value.KeyType
        ?? throw new InvalidOperationException(
            $"No key mapping is defined for type {CollectionName}. Please specify in the configuration of the data sources source.");
    internal Lazy<KeyFunction> Key { get; init; }
}
