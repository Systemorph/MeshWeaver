namespace MeshWeaver.Domain;

/// <summary>
/// This attribute sets type to which current type is mapped in Db
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class TargetTypeAttribute(Type type) : Attribute
{
    public Type Type { get; set; } = type;
}
