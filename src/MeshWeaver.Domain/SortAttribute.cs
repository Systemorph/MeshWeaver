namespace MeshWeaver.Domain;

/// <summary>
/// Marks a member as sortable and configures its default sort behavior in generated UI.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public class SortAttribute : Attribute
{
    /// <summary>
    /// Whether this member is the default field to sort by.
    /// </summary>
    public bool IsDefaultSort;
    /// <summary>
    /// The default sort direction for this member.
    /// </summary>
    public SortDirection SortDirection;
    /// <summary>
    /// Whether the member can be sorted by; defaults to <c>true</c>.
    /// </summary>
    public bool Sortable = true;
}

/// <summary>
/// The direction in which values are sorted.
/// </summary>
public enum SortDirection{
    /// <summary>Sort from smallest to largest.</summary>
    Ascending,
    /// <summary>Sort from largest to smallest.</summary>
    Descending}
