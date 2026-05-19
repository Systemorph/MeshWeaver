using MeshWeaver.Data;
using MeshWeaver.Graph.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides configuration for TrackedChange nodes in the graph.
/// TrackedChange nodes are satellite entities â€” excluded from search and create contexts.
/// Access is delegated to the MainNode (parent) via SatelliteAccessRule.
/// </summary>
public static class TrackedChangeNodeType
{
    /// <summary>
    /// The NodeType value used to identify tracked change nodes.
    /// </summary>
    public const string NodeType = "TrackedChange";

    /// <summary>
    /// Registers the built-in "TrackedChange" MeshNode on the mesh builder.
    /// </summary>
    public static TBuilder AddTrackedChangeType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<INodeTypeAccessRule>(sp =>
                new SatelliteAccessRule(NodeType, sp.GetService<ISecurityService>() ?? new NullSecurityService()));
            return services;
        });
        return builder;
    }

    /// <summary>
    /// Creates a MeshNode definition for the TrackedChange node type.
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "TrackedChange",
        Icon = "/static/NodeTypeIcons/document.svg",
        IsSatelliteType = true,
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<TrackedChange>())
    };
}
