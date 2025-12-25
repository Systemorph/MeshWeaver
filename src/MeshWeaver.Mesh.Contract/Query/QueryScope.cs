namespace MeshWeaver.Mesh.Query;

/// <summary>
/// Defines the scope of a query relative to a path.
/// </summary>
public enum QueryScope
{
    /// <summary>Query only at the exact path specified.</summary>
    Exact,

    /// <summary>Query the path and all descendant paths.</summary>
    Descendants,

    /// <summary>Query the path and all ancestor paths upward.</summary>
    Ancestors,

    /// <summary>Query ancestors, the path, and all descendants.</summary>
    Hierarchy
}
