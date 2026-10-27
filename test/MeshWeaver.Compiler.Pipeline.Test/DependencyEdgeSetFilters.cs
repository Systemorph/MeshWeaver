using System.Linq;
using MeshWeaver.Mesh;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// The edge kinds the NodeType-graph tests are about. A compile now also records
/// <see cref="DependencyEdgeKinds.Platform"/> edges (every NodeType binds the platform through its
/// skeleton) and dynamic ones; the tests that pin the NodeType/Module graph read only those two
/// kinds, and the new kinds have their own tests (<see cref="PlatformPackageAndDynamicEdgesTest"/>).
/// </summary>
internal static class DependencyEdgeSetFilters
{
    /// <summary>The set with only its NodeType and Module edges.</summary>
    /// <param name="set">The extracted set.</param>
    public static DependencyEdgeSet GraphEdges(this DependencyEdgeSet set) =>
        set with
        {
            Edges = [.. set.Edges.Where(e => e.Kind is DependencyEdgeKinds.NodeType or DependencyEdgeKinds.Module)],
        };
}
