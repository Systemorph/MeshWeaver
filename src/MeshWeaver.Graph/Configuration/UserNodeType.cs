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
    /// - Read: granted to all authenticated users (public view rights)
    /// - Create/Update: granted to portal namespace hubs (for onboarding)
    /// </summary>
    private class UserSelfRegistryAccessRule : INodeTypeAccessRule
    {
        public string NodeType => UserNodeType.NodeType;

        public IReadOnlyCollection<NodeOperation> SupportedOperations =>
            [NodeOperation.Create, NodeOperation.Read, NodeOperation.Update];

        public Task<bool> HasAccessAsync(NodeValidationContext context, string? userId, CancellationToken ct = default)
        {
            // All authenticated users can read User nodes (public profile view)
            if (context.Operation == NodeOperation.Read && !string.IsNullOrEmpty(userId))
                return Task.FromResult(true);

            // Users can update their own User node
            if (context.Operation == NodeOperation.Update && !string.IsNullOrEmpty(userId))
            {
                var nodePath = context.Node.Path;
                if (!string.IsNullOrEmpty(nodePath))
                {
                    var userScopePath = $"User/{userId}";
                    if (nodePath.Equals(userScopePath, StringComparison.OrdinalIgnoreCase)
                        || nodePath.StartsWith(userScopePath + "/", StringComparison.OrdinalIgnoreCase))
                        return Task.FromResult(true);
                }
            }

            // Create/Update: portal namespace hubs (onboarding flow)
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
