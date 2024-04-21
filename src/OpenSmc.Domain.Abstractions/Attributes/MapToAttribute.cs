namespace OpenSmc.Domain.Abstractions.Attributes
{
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public class MapToAttribute : Attribute
    {
        public MapToAttribute(string propertyName)
        {
            PropertyName = propertyName;
        }

        public string PropertyName { get; set; }
    }
}
