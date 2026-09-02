using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides configuration for NodeType definition nodes in the graph.
/// These are meta-nodes that describe other node types.
/// </summary>
public static class NodeTypeNodeType
{
    /// <summary>
    /// The NodeType value used to identify node type definition nodes.
    /// </summary>
    public const string NodeType = MeshNode.NodeTypePath;

    /// <summary>
    /// Registers the built-in "NodeType" MeshNode on the mesh builder.
    /// </summary>
    public static TBuilder AddNodeTypeType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        return builder;
    }

    /// <summary>
    /// Creates a MeshNode definition for the NodeType node type.
    /// This provides HubConfiguration for nodes with nodeType="NodeType".
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Node Type",
        Icon = "/static/NodeTypeIcons/code.svg",
        ExcludeFromContext = new HashSet<string> { "search", "content" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<NodeTypeDefinition>())
            .AddNodeTypeView()
            // The dynamic lane of the nodeType → instance-locations projection (#3039): the
            // definition's own hub mirrors its InstanceLocations into the mesh singleton for as
            // long as it is live. The OBSERVABLE overload, as BuildNodeType: the own-node stream
            // must be opened on the init turn, after Build returns, never inside it.
            .WithInitialization(PublishInstanceLocations)
    };

    /// <summary>
    /// Couples <see cref="NodeTypeInstanceLocations.PublishFrom"/> to the definition hub's lifetime.
    /// A static method group, so <c>WithInitialization</c>'s delegate-identity idempotency collapses
    /// repeat registrations from composed configurators.
    /// </summary>
    /// <param name="hub">The definition node's own hub.</param>
    /// <returns>An observable that completes once the mirror is installed.</returns>
    private static IObservable<Unit> PublishInstanceLocations(IMessageHub hub) =>
        Observable.Defer(() =>
        {
            hub.RegisterForDisposal(NodeTypeInstanceLocations.PublishFrom(hub));
            return Observable.Return(Unit.Default);
        });
}
