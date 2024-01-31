namespace OpenSmc.Partition;

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public class PartitionKeyAttribute : Attribute
{
    public PartitionKeyAttribute(string name, Type type)
    {
        PartitionName = name;
        PartitionType = type;
    }

    public PartitionKeyAttribute(string name)
    {
        PartitionName = name;
    }

    public PartitionKeyAttribute(Type type)
    {
        PartitionType = type;
    }

    public PartitionKeyAttribute()
    {
    }

    //rather optional for partitions of EntityTypes
    public Type PartitionType;
    public string PartitionName;
}
