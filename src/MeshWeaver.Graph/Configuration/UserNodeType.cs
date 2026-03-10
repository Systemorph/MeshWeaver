using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
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
    /// Access rules (public read, self-edit, portal create) are defined in the HubConfiguration.
    /// </summary>
    public static TBuilder AddUserType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IStaticNodeProvider, UserNodeProvider>();
            services.AddSingleton<INodeTypeAccessRule, UserAccessRule>();
            services.AddSingleton(new NodeTypePermission(NodeType, PublicRead: true));
            return services;
        });
        return builder;
    }

    private class UserNodeProvider : IStaticNodeProvider
    {
        public IEnumerable<MeshNode> GetStaticNodes()
        {
            yield return CreateMeshNode();
        }
    }

    /// <summary>
    /// Kept for backward compatibility. Access rules are now in HubConfiguration.
    /// </summary>
    public static TBuilder AddSelfRegistry<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
        => builder;

    /// <summary>
    /// Creates a MeshNode definition for the User node type.
    /// Access rules: public read, self-edit, portal create (onboarding).
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
                .WithContentType<User>())
            .WithPublicRead()
            .WithSelfEdit()
            .WithPortalCreate()
            .AddDefaultLayoutAreas()
            .AddUserActivityLayoutAreas()
            .AddLayout(layout => layout.WithDefaultArea(UserActivityLayoutAreas.ActivityArea))
    };

    /// <summary>
    /// Adds a create-access rule for portal namespace hubs (onboarding flow).
    /// Portal hubs (e.g. portal/xxx) can create and update User nodes.
    /// </summary>
    private static MessageHubConfiguration WithPortalCreate(this MessageHubConfiguration config)
        => config.AddAccessRule(
            [NodeOperation.Create, NodeOperation.Update],
            (_, userId) => IsPortalIdentity(userId));

    /// <summary>
    /// DI-registered access rule for User nodes — reliable fallback when hub-config
    /// rules haven't been cached yet (e.g. during first onboarding).
    /// </summary>
    private class UserAccessRule : INodeTypeAccessRule
    {
        public string NodeType => UserNodeType.NodeType;

        public IReadOnlyCollection<NodeOperation> SupportedOperations =>
            [NodeOperation.Create, NodeOperation.Read, NodeOperation.Update];

        public Task<bool> HasAccessAsync(NodeValidationContext context, string? userId, CancellationToken ct = default)
        {
            // Read: all users including anonymous
            if (context.Operation == NodeOperation.Read)
                return Task.FromResult(true);

            if (string.IsNullOrEmpty(userId))
                return Task.FromResult(false);

            // Update: user can edit their own node
            if (context.Operation == NodeOperation.Update)
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

            // Create/Update: portal namespace identities (onboarding flow)
            return Task.FromResult(IsPortalIdentity(userId));
        }
    }

    private static bool IsPortalIdentity(string? userId)
    {
        if (string.IsNullOrEmpty(userId)) return false;
        var innerAddress = userId;
        var tildeIndex = userId.LastIndexOf('~');
        if (tildeIndex >= 0)
            innerAddress = userId[(tildeIndex + 1)..];
        return innerAddress.StartsWith(PortalNamespace + "/", StringComparison.OrdinalIgnoreCase);
    }
}
