namespace MeshWeaver.Mesh;

/// <summary>
/// Defines the scope of a query relative to a path.
/// </summary>
public enum QueryScope
{
    /// <summary>Query only at the exact path specified.</summary>
    Exact,

    /// <summary>Query immediate children of the path (one level deep, excludes self).</summary>
    Children,

    /// <summary>Query all descendant paths recursively (excludes self).</summary>
    Descendants,

    /// <summary>Query all ancestor paths upward (excludes self).</summary>
    Ancestors,

    /// <summary>Query ancestors, the path, and all descendants.</summary>
    Hierarchy,

    /// <summary>Query self and all descendant paths recursively (subtree).</summary>
    Subtree,

    /// <summary>Query self and all ancestor paths upward.</summary>
    AncestorsAndSelf,

    /// <summary>
    /// Query the <b>next populated level</b> below the path — the nearest real nodes,
    /// skipping empty intermediate namespace segments. For base <c>P</c>, returns every
    /// active node strictly below <c>P</c> with no other active node between it and <c>P</c>
    /// (e.g. <c>a/b/node</c> surfaces directly at the root when <c>a</c> and <c>a/b</c> are not
    /// real nodes). Backed by a single Postgres anti-join; see <see cref="NamespaceFrontier"/>
    /// for the in-memory equivalent. The drill primitive for graph navigation.
    /// </summary>
    NextLevel
}