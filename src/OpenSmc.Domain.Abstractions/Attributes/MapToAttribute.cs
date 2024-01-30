namespace OpenSmc.Domain.Abstractions.Attributes
{

    public class MapToAttribute : Attribute
    {
        public MapToAttribute(string propertyName)
        {
            PropertyName = propertyName;
        }
        public string PropertyName { get; set; }
    }
}