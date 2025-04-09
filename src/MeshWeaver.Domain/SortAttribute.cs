namespace MeshWeaver.Domain;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public class SortAttribute : Attribute
{
    public bool IsDefaultSort;
    public SortDirection SortDirection;
    public bool Sortable = true;
}

public enum SortDirection{Ascending, Descending}
