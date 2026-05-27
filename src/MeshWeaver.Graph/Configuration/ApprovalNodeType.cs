using MeshWeaver.Data;
using MeshWeaver.Messaging;
using MeshWeaver.Graph.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides configuration for Approval nodes in the graph.
/// Approval nodes are system-generated â€” excluded from search and create contexts.
/// Access is delegated to the MainNode (parent) via SatelliteAccessRule.
/// </summary>
public static class ApprovalNodeType
{
    /// <summary>
    /// The NodeType value used to identify approval nodes.
    /// </summary>
    public const string NodeType = "Approval";

    /// <summary>
    /// Registers the built-in "Approval" MeshNode on the mesh builder.
    /// </summary>
    public static TBuilder AddApprovalType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<INodeTypeAccessRule>(sp =>
                new SatelliteAccessRule(NodeType, sp.GetRequiredService<IMessageHub>()));
            return services;
        });
        return builder;
    }

    /// <summary>
    /// Creates a MeshNode definition for the Approval node type.
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Approval",
        Icon = "/static/NodeTypeIcons/checkmark.svg",
        IsSatelliteType = true,
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        HubConfiguration = config => config
            .AddApprovalViews()
            .AddMeshDataSource(source => source
                .WithContentType<Approval>())
    };
}
