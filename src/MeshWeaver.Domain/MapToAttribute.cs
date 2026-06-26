namespace MeshWeaver.Domain
{
    /// <summary>
    /// Maps the annotated property to a differently named property on the mapped target.
    /// </summary>
    /// <param name="propertyName">The name of the target property this property maps to.</param>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public class MapToAttribute(string propertyName) : Attribute
    {
        /// <summary>
        /// The name of the target property this property maps to.
        /// </summary>
        public string PropertyName { get; set; } = propertyName;
    }
}
