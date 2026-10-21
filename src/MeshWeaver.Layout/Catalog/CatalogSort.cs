namespace MeshWeaver.Layout;

/// <summary>
/// One order of a catalog (<see cref="Controls.Catalog"/>): a <paramref name="Label"/> and the
/// <c>sort:</c> term the STORE applies (<c>sort:name</c>, <c>sort:lastModified-desc</c>). The catalog
/// appends its default to the filter; a reader types any other into the search bar.
/// </summary>
/// <param name="Label">A human name for the order (e.g. "Last activity").</param>
/// <param name="Sort">The store-side sort term, e.g. <c>sort:name</c>.</param>
public partial record CatalogSort(string Label, string Sort)
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

    /// <summary>
    /// <paramref name="filter"/> with <paramref name="sort"/> appended — the one place a catalog query
    /// gets its order. A UNION filter (one sub-query per line) gets it on EVERY line: the view combines
    /// each line with the typed text and the engine takes the union's order from the FIRST line
    /// (<c>MeshQueryRequest.FromQueries</c>), so a sort on the last line alone left the union unsorted.
    /// A line that already carries its own <c>sort:</c> keeps it; a <c>sort:</c> typed into the bar
    /// still wins, because the view appends the typed text after each line and the parser keeps the
    /// last. Pure.
    /// </summary>
    /// <param name="filter">The set, one query or a newline-separated union of queries.</param>
    /// <param name="sort">The store-side sort term, e.g. <c>sort:name</c>.</param>
    public static string Sorted(string filter, string sort)
    {
        var lines = filter.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
            return sort;
        return string.Join("\n", lines.Select(line => HasSort(line) ? line : $"{line} {sort}"));
    }

    private static bool HasSort(string line) => SortTerm().IsMatch(line);

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<=^|\s)sort:\S", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex SortTerm();
}
