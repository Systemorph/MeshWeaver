namespace MeshWeaver.Layout.Pivot;

/// <summary>
/// Specifies the default sort order for a dimension when it's used in a pivot grid
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class DefaultSortOrderAttribute(SortOrder sortOrder) : Attribute
{
    /// <summary>The default sort order to apply when this dimension or aggregate is first used in a pivot grid.</summary>
    public SortOrder SortOrder { get; } = sortOrder;
}
