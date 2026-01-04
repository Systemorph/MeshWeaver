namespace MeshWeaver.Mesh.Query;

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
    AncestorsAndSelf
}