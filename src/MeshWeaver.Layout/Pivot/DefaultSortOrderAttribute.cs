namespace MeshWeaver.Layout.Pivot;

/// <summary>
/// Specifies the default sort order for a dimension when it's used in a pivot grid
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class DefaultSortOrderAttribute(SortOrder sortOrder) : Attribute
{
    public SortOrder SortOrder { get; } = sortOrder;
}
