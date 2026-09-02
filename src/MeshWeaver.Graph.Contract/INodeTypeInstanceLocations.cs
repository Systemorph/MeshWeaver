namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// A NodeType's DECLARED instance locations — the <c>namespace:</c>/<c>path:</c> query strings
/// <see cref="NodeTypeDefinition.InstanceLocations"/> carries, projected <c>nodeType → locations</c>
/// for the storage layer (#3039, parts 1–2 of Plugins#1127). An unanchored <c>nodeType:X</c> query
/// is then answered from the declared locations' partitions instead of by UNION-ing every partition
/// schema — the fourth fan-out narrowing, whose planner lives in MeshWeaver.Plugins
/// (<c>PostgreSqlPartitionedMeshQuery</c>) and INTERSECTS the declared set with the schemas the
/// query was going to run, never substitutes it.
///
/// <para><b>The contract is fail-open, and every clause of it is load-bearing.</b> A type this
/// source does not know — <see langword="null"/> or an empty list — is a type whose query runs the
/// full fan-out, exactly as it does today. A schema wrongly dropped from a fan-out is a row that
/// silently never appears, so an undeclared, newly-installed or mis-declared type must be answered
/// SLOWLY, never PARTIALLY. An OVER-stated declaration costs one zero-row branch; an UNDER-stated
/// one silently loses rows, which is why the security fold's own types
/// (<see cref="Mesh.Security.NeverNarrowedNodeTypes"/>) may never carry a declaration at all.</para>
///
/// <para>The in-box implementation is <c>NodeTypeInstanceLocations</c> (MeshWeaver.Graph): the
/// static definitions registered on the mesh builder, plus every dynamic definition whose own hub
/// is live on this process. Registered in DI by <c>AddGraph()</c>.</para>
/// </summary>
public interface INodeTypeInstanceLocations
{
    /// <summary>
    /// The declared instance-location queries for <paramref name="nodeType"/> — each a mesh query
    /// string whose <c>namespace:</c>/<c>path:</c> leg names where instances live
    /// (<c>namespace:Admin/Menu</c>, <c>namespace:A|B|C</c>, <c>path:Ops/Mail</c>).
    /// <see langword="null"/> or empty means NOT DECLARED, which means "fan out over everything".
    /// </summary>
    /// <param name="nodeType">The node type being queried, exactly as it appears in <c>nodeType:</c>.</param>
    /// <returns>The declared location queries, or null when the type declares none.</returns>
    IReadOnlyList<string>? LocationsFor(string nodeType);
}
