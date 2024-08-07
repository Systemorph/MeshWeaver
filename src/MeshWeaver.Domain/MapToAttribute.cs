namespace MeshWeaver.Domain
{
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public class MapToAttribute(string propertyName) : Attribute
    {
        public string PropertyName { get; set; } = propertyName;
    }
}
