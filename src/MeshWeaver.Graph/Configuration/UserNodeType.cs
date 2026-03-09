using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides configuration for User nodes in the graph.
/// User nodes represent people with access to the system.
/// Instances can be created anywhere in the node hierarchy.
/// </summary>
public static class UserNodeType
{
    /// <summary>
    /// The NodeType value used to identify user nodes.
    /// </summary>
    public const string NodeType = "User";

    /// <summary>
    /// The portal namespace prefix. Hubs in this namespace can create/read/edit User nodes
    /// when self-registry is enabled.
    /// </summary>
    public const string PortalNamespace = "portal";

    /// <summary>
    /// Registers the built-in "User" MeshNode on the mesh builder.
    /// </summary>
    public static TBuilder AddUserType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        return builder;
    }

    /// <summary>
    /// Enables self-registry: portal namespace hubs can create/read/update User nodes.
    /// This allows the onboarding flow to create User nodes on behalf of authenticated users.
    /// </summary>
    public static TBuilder AddSelfRegistry<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<INodeTypeAccessRule, UserSelfRegistryAccessRule>();
            return services;
        });
        return builder;
    }

    /// <summary>
    /// Creates a MeshNode definition for the User node type.
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "User",
        Icon = "/static/NodeTypeIcons/person.svg",
        NodeType = NodeType,
        AssemblyLocation = typeof(UserNodeType).Assembly.Location,
        Content = new NodeTypeDefinition { DefaultNamespace = "User", RestrictedToNamespaces = ["User"] },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<AccessObject>())
            .AddDefaultLayoutAreas()
            .AddUserActivityLayoutAreas()
            .AddLayout(layout => layout.WithDefaultArea(UserActivityLayoutAreas.ActivityArea))
    };

    /// <summary>
    /// Access rule for User nodes when self-registry is enabled.
    /// Allows portal namespace hubs to create/read/update User nodes (for onboarding).
    /// Falls through to standard RLS for other identities.
    /// </summary>
    private class UserSelfRegistryAccessRule : INodeTypeAccessRule
    {
        public string NodeType => UserNodeType.NodeType;

        public IReadOnlyCollection<NodeOperation> SupportedOperations =>
            [NodeOperation.Create, NodeOperation.Read, NodeOperation.Update];

        public Task<bool> HasAccessAsync(NodeValidationContext context, string? userId, CancellationToken ct = default)
        {
            if (!string.IsNullOrEmpty(userId))
            {
                var innerAddress = userId;
                var tildeIndex = userId.LastIndexOf('~');
                if (tildeIndex >= 0)
                    innerAddress = userId[(tildeIndex + 1)..];

                if (innerAddress.StartsWith(PortalNamespace + "/", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }
    }
}
