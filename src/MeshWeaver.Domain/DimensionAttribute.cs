namespace MeshWeaver.Domain;

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public class DimensionAttribute(Type type, string name = null) : Attribute
{
    public string Name { get; } = name ?? type.Name;
    public Type Type { get; } = type;
}


public class DimensionAttribute<T>(string name=null) : DimensionAttribute(typeof(T), name);
