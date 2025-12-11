using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph;

/// <summary>
/// Registers the Graph application with the mesh catalog.
/// Provides a 4-level hub hierarchy:
/// 1. @graph - Top level, lists all organizations
/// 2. @graph/{org} - Organization hub, lists all namespaces
/// 3. @graph/{org}/{namespace} - Namespace hub, search for vertices
/// 4. @graph/{org}/{namespace}/{type}/{id} - Vertex hub with satellites
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public class GraphApplicationAttribute : MeshNodeAttribute
{
    public const string GraphType = "graph";

    public static readonly Address Address = new(GraphType);

    public override IEnumerable<MeshNode> Nodes =>
    [
        CreateFromHubConfiguration(
            Address,
            "Graph",
            GraphHubConfiguration.ConfigureGraphHub
        )
    ];

    public override IEnumerable<MeshNamespace> Namespaces =>
    [
        new MeshNamespace(GraphType, "Graph")
        {
            Description = "Graph-based information network",
            IconName = "GraphBranching",
            DisplayOrder = 10,
            MinSegments = 1, // At minimum org name
            AutocompleteAddress = _ => Address,
            Factory = CreateGraphNode
        }
    ];

    private static MeshNode? CreateGraphNode(Address address)
    {
        if (address.Type != GraphType)
            return null;

        var segments = address.Segments;

        return segments.Length switch
        {
            // @graph/{org} - Organization hub
            1 => new MeshNode(address.Type, address.Id, $"Organization: {segments[0]}")
            {
                HubConfiguration = config => GraphHubConfiguration.ConfigureOrganizationHub(config, segments[0])
            },
            // @graph/{org}/{namespace} - Namespace hub
            2 => new MeshNode(address.Type, address.Id, $"Namespace: {segments[0]}/{segments[1]}")
            {
                HubConfiguration = config => GraphHubConfiguration.ConfigureNamespaceHub(config, segments[0], segments[1])
            },
            // @graph/{org}/{namespace}/{type}/{id} - Vertex hub
            >= 4 => new MeshNode(address.Type, address.Id, $"Vertex: {string.Join("/", segments)}")
            {
                HubConfiguration = config => GraphHubConfiguration.ConfigureVertexHub(
                    config, segments[0], segments[1], segments[2], segments[3])
            },
            _ => null
        };
    }
}
