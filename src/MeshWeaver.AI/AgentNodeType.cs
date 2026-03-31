using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.AI;

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
    /// Registers the built-in "Agent" MeshNode on the mesh builder
    /// and a static node provider for built-in agents (e.g., ThreadNamer).
    /// </summary>
    public static TBuilder AddAgentType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.ConfigureNodeTypeAccess(a => a.WithPublicRead(NodeType));
        builder.ConfigureServices(services =>
            services.AddSingleton<IStaticNodeProvider, BuiltInAgentProvider>());
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
