using System;

namespace OpenSmc.Partition
{
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public class PartitionIdAttribute : Attribute
    {
    }
}
