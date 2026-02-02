namespace MeshWeaver.Domain;

/// <summary>
/// Marks a property as mapping to a MeshNode property.
/// When content is synced to MeshNode, properties with this attribute
/// will have their values copied to the specified MeshNode property.
/// When MeshNode is updated, the values can be synced back to the content.
/// </summary>
/// <param name="meshNodeProperty">The MeshNode property to map to (e.g., "Name", "Description", "Icon", "Category").</param>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public class MeshNodePropertyAttribute(string meshNodeProperty) : Attribute
{
    /// <summary>
    /// The MeshNode property name to map this property to.
    /// Valid values: "Name", "Description", "Category", "Icon"
    /// </summary>
    public string MeshNodeProperty { get; } = meshNodeProperty;
}
