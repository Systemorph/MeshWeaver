using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides configuration for Agent nodes in the graph.
/// Agent nodes represent AI agent configurations.
/// </summary>
public static class AgentNodeType
{
    /// <summary>
    /// The NodeType value used to identify agent nodes.
    /// </summary>
    public const string NodeType = "Agent";

    /// <summary>
    /// Registers the built-in "Agent" MeshNode on the mesh builder.
    /// </summary>
    public static TBuilder AddAgentType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        return builder;
    }

    /// <summary>
    /// Creates a MeshNode definition for the Agent node type.
    /// This provides HubConfiguration for nodes with nodeType="Agent".
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Agent",
        Icon = "/static/NodeTypeIcons/bot.svg",
        AssemblyLocation = typeof(AgentNodeType).Assembly.Location,
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<AgentConfiguration>())
            .AddAgentView()
    };
}
