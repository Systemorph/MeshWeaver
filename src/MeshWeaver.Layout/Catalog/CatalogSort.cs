namespace MeshWeaver.Layout;

/// <summary>
/// One order of a catalog (<see cref="Controls.Catalog"/>): a <paramref name="Label"/> and the
/// <c>sort:</c> term the STORE applies (<c>sort:name</c>, <c>sort:lastModified-desc</c>). The catalog
/// appends its default to the filter; a reader types any other into the search bar.
/// </summary>
/// <param name="Label">A human name for the order (e.g. "Last activity").</param>
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

    /// <summary>The well-known orders, in the order a placeholder offers them.</summary>
    public static readonly IReadOnlyList<CatalogSort> WellKnown = [LastActivity, Name, LastChanged];

    /// <summary>The search placeholder of a catalog: what it searches, and — by example — that the
    /// query language works in the same bar (the orders other than <paramref name="current"/>). Pure.</summary>
    public static string Placeholder(string title, CatalogSort current) =>
        $"Search {title.ToLowerInvariant()} — e.g. a name, "
        + string.Join(", ", WellKnown.Where(s => s.Sort != current.Sort).Select(s => s.Sort));

    /// <summary><paramref name="filter"/> with <paramref name="sort"/> appended — the one place a
    /// catalog query gets its order. Pure.</summary>
    public static string Sorted(string filter, string sort) => $"{filter.TrimEnd()} {sort}";
}
