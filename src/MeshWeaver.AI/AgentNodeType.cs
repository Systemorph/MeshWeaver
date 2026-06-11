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
    public static TBuilder AddAgentType<TBuilder>(this TBuilder builder,
        IReadOnlySet<string>? serveFromPartition = null) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.ConfigureNodeTypeAccess(a => a.WithPublicRead(NodeType));
        // When the "Agent" partition is DB-synced (static-repo import), DO NOT register the
        // read-only in-memory static surfaces — they would shadow Postgres (specific wins over
        // the wildcard PG provider) and reject the import's writes. The import materializes the
        // agents into the partition; PG serves them. Otherwise (monolith, un-synced deploys)
        // keep the in-memory read-only surfaces. The BuiltInAgentProvider singleton itself stays
        // registered either way — the import SOURCE (AgentStaticRepoSource) wraps it to read the
        // built-in agents. See Doc/Architecture/StaticRepoImport.md.
        //
        // 🚨 BOTH the IStaticNodeProvider AND the IPartitionStorageProvider must be gated on
        // !dbSynced. The IStaticNodeProvider feeds serviceProvider.FindStaticNode(path); leaving
        // it registered while synced made the importer's inner CreateNode see the built-in agent
        // as already-present and fail "Node already exists at path: Agent/X" — so the Agent
        // partition never materialized into the DB (atioz 2026-06-11: Agent imported 4 / failed 8
        // while Doc — which has no IStaticNodeProvider — imported 161/0). Gating only the storage
        // provider (the prior state) was the gap. The "Agent" NodeType definition itself stays via
        // AddMeshNodes(CreateMeshNode()) above, so the import's NodeType-existence check still
        // resolves. See OrleansStaticRepoImportStaticBackedTest.
        var dbSynced = serveFromPartition?.Contains("Agent") == true;
        builder.ConfigureServices(services =>
        {
            services.TryAddSingleton<BuiltInAgentProvider>();
            if (!dbSynced)
            {
                services.AddSingleton<IStaticNodeProvider>(sp => sp.GetRequiredService<BuiltInAgentProvider>());
                services.AddSingleton<IPartitionStorageProvider>(sp =>
                    new StaticNodePartitionStorageProvider(
                        "Agent",
                        sp.GetRequiredService<BuiltInAgentProvider>(),
                        description: "Built-in agent definitions (read-only)."));
            }
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
        // Agents are first-class content nodes — they live at top-level paths
        // (e.g. namespace:Agent for built-ins, or per-partition under contextPath)
        // and are NOT children of another entity. The synced picker query
        // (AgentPickerProjection.ObserveAgents) treats them as standalone.
        IsSatelliteType = false,
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<AgentConfiguration>())
            .AddAgentView()
    };
}
