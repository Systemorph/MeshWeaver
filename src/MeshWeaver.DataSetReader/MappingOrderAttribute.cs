namespace MeshWeaver.DataSetReader;

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public class MappingOrderAttribute(int order) : Attribute
{
    public int Length { get; set; }
    public int Order { get; set; } = order;
}
