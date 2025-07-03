using System.ComponentModel.DataAnnotations;
using System.Reflection;
using MeshWeaver.Domain;
using MeshWeaver.Utils;
using Namotion.Reflection;

namespace MeshWeaver.Messaging.Serialization;

public record TypeDefinition : ITypeDefinition
{
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

    public TypeDefinition(Type elementType, string typeName, KeyFunctionBuilder keyFunctionBuilder, Address address)
        : this(elementType, typeName, keyFunctionBuilder)
    {
        Address = address;
    }


    public Type Type { get; }
    public string DisplayName { get; }
    public string CollectionName { get; }
    public object? Icon { get; init; }
    public Address? Address { get; init; }

    public int? Order { get; }
    public string? GroupName { get; }
    public string Description { get; init; }

    public virtual object GetKey(object instance) =>
        Key.Value.Function(instance)
        ?? throw new InvalidOperationException(
            $"No key mapping is defined for type {CollectionName}. Please specify in the configuration of the data sources source.");

    public Type GetKeyType() =>
        Key.Value.KeyType
        ?? throw new InvalidOperationException(
            $"No key mapping is defined for type {CollectionName}. Please specify in the configuration of the data sources source.");
    internal Lazy<KeyFunction> Key { get; init; }
}
