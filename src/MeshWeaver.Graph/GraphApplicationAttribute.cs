using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph;

/// <summary>
/// Registers the Graph application with the mesh catalog.
/// Graph nodes are registered at the organization level.
/// Addresses follow the pattern: {org}/{namespace}/{type}/{id}
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public class GraphApplicationAttribute : MeshNodeAttribute
{
    // Graph no longer uses a special prefix - nodes are registered directly at the org level
    // Example: acme/products/Product/123 instead of graph/acme/products/Product/123

    public override IEnumerable<MeshNode> Nodes => [];
}
