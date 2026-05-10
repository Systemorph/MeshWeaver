using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
    /// plus the partition routing for built-in agents (e.g., ThreadNamer).
    /// The "Agent" partition's storage of record is the
    /// <see cref="BuiltInAgentProvider"/>'s output — wrapped in a
    /// <see cref="StaticNodePartitionStorageProvider"/> so it goes through
    /// the same first-match-wins routing as every other partition.
    /// Also kept as an <see cref="IStaticNodeProvider"/> for the legacy
    /// consumers (StaticNodeQueryProvider, MeshDataSource fallback) that
    /// still iterate that DI collection directly.
    /// </summary>
    public static TBuilder AddAgentType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.ConfigureNodeTypeAccess(a => a.WithPublicRead(NodeType));
        builder.ConfigureServices(services =>
        {
            services.TryAddSingleton<BuiltInAgentProvider>();
            services.AddSingleton<IStaticNodeProvider>(sp => sp.GetRequiredService<BuiltInAgentProvider>());
            services.AddSingleton<IPartitionStorageProvider>(sp =>
                new StaticNodePartitionStorageProvider(
                    "Agent",
                    sp.GetRequiredService<BuiltInAgentProvider>(),
                    description: "Built-in agent definitions (read-only)."));
            return services;
        });
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
