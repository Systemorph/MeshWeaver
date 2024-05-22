namespace OpenSmc.Domain;

/// <summary>
/// This attribute sets type to which current type is mapped in Db
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class TargetTypeAttribute : Attribute
{
    public TargetTypeAttribute(Type type)
    {
        Type = type;
    }

    public Type Type { get; set; }
}
