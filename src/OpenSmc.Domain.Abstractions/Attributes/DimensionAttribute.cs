namespace OpenSmc.Domain.Abstractions.Attributes
{
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public class DimensionAttribute : Attribute
    {
        public string Name { get; }
        public Type Type { get; }

        public DimensionAttribute(Type type, string name = null) 
        {
            Type = type;
            Name = name ?? type.Name;
        }
    }
}
