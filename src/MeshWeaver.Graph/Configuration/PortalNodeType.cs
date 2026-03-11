using MeshWeaver.ContentCollections;
using MeshWeaver.Graph.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides configuration for Portal session nodes in the graph.
/// Portal nodes are ephemeral satellite nodes created when a portal session starts
/// and deleted when it ends. Access is delegated to the MainNode (parent) via SatelliteAccessRule.
/// </summary>
public static class PortalNodeType
{
    public const string NodeType = "Portal";

    public static TBuilder AddPortalType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
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
    /// Creates the Portal satellite type MeshNode definition.
    /// HubConfiguration provides standard portal services (content collections).
    /// PortalApplication may add additional configuration (navigation, routing) at hub creation time.
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Portal Session",
        IsSatelliteType = true,
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        AssemblyLocation = typeof(PortalNodeType).Assembly.Location,
        HubConfiguration = config => config
            .AddContentCollections()
    };
}
