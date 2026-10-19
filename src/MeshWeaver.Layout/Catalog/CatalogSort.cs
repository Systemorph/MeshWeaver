namespace MeshWeaver.Layout;

/// <summary>
/// One Sort-by choice of a catalog (<see cref="Controls.Catalog"/>): a <paramref name="Label"/> and the
/// <c>sort:</c> term the STORE applies (<c>sort:name</c>, <c>sort:lastModified-desc</c>). The catalog
/// appends it to its filter, so re-sorting never changes WHICH nodes show — only their order.
/// </summary>
/// <param name="Label">The label shown in the "Sort by" dropdown.</param>
/// <param name="Sort">The store-side sort term, e.g. <c>sort:name</c>.</param>
public record CatalogSort(string Label, string Sort)
{
    /// <summary>Newest change ANYWHERE under the node first — a container's activity, not its own
    /// stamp (<c>sort:subtreeLastModified-desc</c>, MeshWeaver#6942).</summary>
    public static readonly CatalogSort LastActivity = new("Last activity", "sort:subtreeLastModified-desc");

    /// <summary>The node's own <c>lastModified</c>, newest first.</summary>
    public static readonly CatalogSort LastChanged = new("Last changed", "sort:lastModified-desc");

    /// <summary>Alphabetical by name.</summary>
    public static readonly CatalogSort Name = new("Name", "sort:name");

    /// <summary>The default choices, in order: last activity, name, last changed.</summary>
    public static readonly IReadOnlyList<CatalogSort> Default = [LastActivity, Name, LastChanged];

    /// <summary><paramref name="filter"/> with <paramref name="sort"/> appended — the one place a
    /// catalog query gets its order. Pure.</summary>
    public static string Sorted(string filter, string sort) => $"{filter.TrimEnd()} {sort}";
}
