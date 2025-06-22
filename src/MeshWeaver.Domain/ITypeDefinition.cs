
namespace MeshWeaver.Domain;

public interface ITypeDefinition
{
    Type Type { get; }
    string DisplayName { get; }
    string CollectionName { get; }
    object Icon { get; }
    object GetKey(object instance);
    int? Order { get; }
    string GroupName { get; }
    string Description { get; }
    Type GetKeyType();

}



