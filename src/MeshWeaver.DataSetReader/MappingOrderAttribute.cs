namespace MeshWeaver.DataSetReader;

/// <summary>
/// Declares the position of a property when mapping it to a fixed sequence of data-set columns.
/// </summary>
/// <param name="order">The zero-based mapping position of the decorated property.</param>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public class MappingOrderAttribute(int order) : Attribute
{
    /// <summary>
    /// Number of columns the property spans; used when a single property maps to a list of values.
    /// </summary>
    public int Length { get; set; }

    /// <summary>
    /// The mapping position of the property; lower values are mapped first.
    /// </summary>
    public int Order { get; set; } = order;
}
