
namespace MeshWeaver.Domain;

public interface ITypeDefinition
{
    Type Type { get; }
    string DisplayName { get; }
    string CollectionName { get; }
    object Icon { get; }
    object GetKey(object instance);
    int? Order { get; }
    string Description { get; }
    string GroupName { get; }
    string GetDescription(string memberName);

}



