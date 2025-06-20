using MeshWeaver.Messaging;

namespace MeshWeaver.AI;

/// <summary>
/// Type definition for AI plugins, separate from the domain type registry
/// </summary>
public record TypeDefinition
{
    public TypeDefinition(Type type, string collectionName, Address address)
    {
        Type = type;
        CollectionName = collectionName;
        Address = address;
        DisplayName = type.Name;
    }

    public TypeDefinition(Type type, string collectionName, Address address, string displayName)
    {
        Type = type;
        CollectionName = collectionName;
        Address = address;
        DisplayName = displayName;
    }

    public Type Type { get; }
    public string CollectionName { get; }
    public Address Address { get; }
    public string DisplayName { get; }
}
