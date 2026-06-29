using MeshWeaver.AI;
using MeshWeaver.Messaging;
using MeshWeaver.ContentCollections;
using MeshWeaver.Graph.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Blazor.Portal;

/// <summary>
/// Provides configuration for Portal session nodes in the graph.
/// Portal nodes are ephemeral satellite nodes created when a portal session starts
/// and deleted when it ends. Access is delegated to the MainNode (parent) via SatelliteAccessRule.
/// Lives in Blazor.Portal so the HubConfiguration can reference AI types.
/// </summary>
public static class PortalNodeType
{
    /// <summary>
    /// The node type identifier (<c>Portal</c>) for portal session satellite nodes.
    /// </summary>
    public const string NodeType = "Portal";

    /// <summary>
    /// Registers the Portal satellite node type on the mesh builder, excluding it from autocomplete
    /// and wiring up a <c>SatelliteAccessRule</c> that delegates access to the parent MainNode.
    /// </summary>
    /// <typeparam name="TBuilder">The mesh builder type being configured.</typeparam>
    /// <param name="builder">The mesh builder to configure.</param>
    /// <returns>The same <paramref name="builder"/> instance for chaining.</returns>
    public static TBuilder AddPortalType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
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
    /// Creates the Portal satellite type MeshNode definition.
    /// HubConfiguration provides standard portal services (content collections)
    /// and registers AI types so the portal hub can deserialize AI messages.
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Portal Session",
        IsSatelliteType = true,
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        HubConfiguration = config =>
        {
            config.TypeRegistry.AddAITypes();
            return config.AddContentCollections();
        }
    };
}
