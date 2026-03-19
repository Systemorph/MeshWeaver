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
            services.AddSingleton<INodePostCreationHandler>(sp =>
                new UserScopeGrantHandler(
                    sp.GetService<ISecurityService>() ?? new NullSecurityService()));
            return services;
        });
        builder.ConfigureNodeTypeAccess(a => a.WithPublicRead(NodeType));
        // nodeType:User → restrict to "User" partition (no fan-out needed)
        builder.AddQueryRoutingRule(query =>
            query.ExtractNodeType() == NodeType ? new QueryRoutingHints { Partition = "User" } : null);
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
            .WithUserNodePublicRead()
            .WithSelfEdit()
            .WithPortalCreate()
            .AddDefaultLayoutAreas()
            .AddUserActivityLayoutAreas()
            .AddGlobalAdminSettingsTab()
            .AddLayout(layout => layout.WithDefaultArea(UserActivityLayoutAreas.ActivityArea))
    };

    /// <summary>
    /// Grants public read access ONLY on the User node itself (path == "User/{id}"),
    /// not on its children (threads, activities, etc.). Children inherit normal
    /// access control — the user gets read access via UserScopeGrantHandler.
    /// </summary>
    private static MessageHubConfiguration WithUserNodePublicRead(this MessageHubConfiguration config)
        => config.AddAccessRule(
            [NodeOperation.Read],
            (context, userId) =>
            {
                if (string.IsNullOrEmpty(userId)) return false;
                var nodePath = context.Node.Path;
                if (string.IsNullOrEmpty(nodePath)) return false;
                // Only the User node itself is public, not children
                // "User/Alice" is public, "User/Alice/SomeThread" is not
                return nodePath.StartsWith("User/", StringComparison.OrdinalIgnoreCase)
                       && !nodePath["User/".Length..].Contains('/');
            });

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
            // Read: only the User node itself is publicly readable (path == "User/{id}").
            // Children (threads, activities, etc.) require explicit access.
            if (context.Operation == NodeOperation.Read)
            {
                var nodePath = context.Node.Path;
                if (!string.IsNullOrEmpty(nodePath)
                    && nodePath.StartsWith("User/", StringComparison.OrdinalIgnoreCase)
                    && !nodePath["User/".Length..].Contains('/'))
                    return Task.FromResult(true);
                return Task.FromResult(false);
            }

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

    private const string GlobalAdminTab = "GlobalAdmin";

    /// <summary>
    /// Adds a "Global Administration" tab to the User node's Settings page.
    /// Only visible when the viewer is the node owner and has Admin permissions at root level.
    /// Shows root-level access assignments for managing global admin roles.
    /// </summary>
    private static MessageHubConfiguration AddGlobalAdminSettingsTab(this MessageHubConfiguration config)
        => config.AddSettingsMenuItems(
            new SettingsMenuItemProvider(GetGlobalAdminTabAsync));

    private static async IAsyncEnumerable<SettingsMenuItemDefinition> GetGlobalAdminTabAsync(
        LayoutAreaHost host, RenderingContext ctx)
    {
        // Check if the viewer is the node owner
        var hubPath = host.Hub.Address.ToString();
        var nodeOwnerId = hubPath.StartsWith("User/") ? hubPath[5..] : hubPath;
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var viewerId = accessService?.Context?.ObjectId
                       ?? accessService?.CircuitContext?.ObjectId;

        if (string.IsNullOrEmpty(viewerId)
            || !string.Equals(viewerId, nodeOwnerId, StringComparison.OrdinalIgnoreCase))
            yield break;

        // Check if the user has Admin permissions at root level
        var securityService = host.Hub.ServiceProvider.GetService<ISecurityService>();
        if (securityService == null)
            yield break;

        var rootPerms = await securityService.GetEffectivePermissionsAsync("", viewerId);
        if (!rootPerms.HasFlag(Permission.All))
            yield break;

        yield return new SettingsMenuItemDefinition(
            Id: GlobalAdminTab,
            Label: "Global Administration",
            ContentBuilder: BuildGlobalAdminTab,
            Group: "Administration",
            Icon: Application.Styles.FluentIcons.Shield(),
            GroupIcon: Application.Styles.FluentIcons.Shield(),
            Order: 300);
    }

    private static UiControl BuildGlobalAdminTab(LayoutAreaHost host, StackControl stack, MeshNode? node)
    {
        // Access Assignments section
        stack = stack.WithView(Controls.Html(
            "<div style=\"font-size: 1.05rem; font-weight: 600; margin-bottom: 16px;\">Global Access Assignments</div>" +
            "<p style=\"color: var(--neutral-foreground-hint); margin-bottom: 16px;\">Manage who has administrative access across the platform.</p>"));

        stack = stack.WithView(Controls.MeshSearch
            .WithHiddenQuery("namespace:_Access nodeType:AccessAssignment")
            .WithShowSearchBox(false)
            .WithShowEmptyMessage(true)
            .WithRenderMode(MeshSearchRenderMode.Flat)
            .WithCollapsibleSections(false)
            .WithSectionCounts(false)
            .WithCreateNodeType("AccessAssignment")
            .WithCreateNamespace("_Access")
            .WithMaxColumns(2));

        // Data Sources section
        stack = stack.WithView(Controls.Html(
            "<div style=\"margin-top: 24px; padding-top: 16px; border-top: 1px solid var(--neutral-stroke-divider);\">" +
            "<div style=\"font-size: 1.05rem; font-weight: 600; margin-bottom: 12px;\">Data Sources</div></div>"));

        var meshService = host.Hub.ServiceProvider.GetService<IMeshService>();
        if (meshService != null)
        {
            stack = stack.WithView(Controls.MeshSearch
                .WithHiddenQuery($"namespace:{MeshDataSourceNodeType.SourcesNamespace} nodeType:{MeshDataSourceNodeType.NodeType}")
                .WithShowSearchBox(false)
                .WithShowEmptyMessage(true)
                .WithRenderMode(MeshSearchRenderMode.Flat)
                .WithCollapsibleSections(false)
                .WithSectionCounts(false)
                .WithMaxColumns(2));
        }

        return stack;
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

    /// <summary>
    /// Post-creation handler that grants the user Read access on their own User/{userId} scope.
    /// Materialized into user_effective_permissions so the standard access control SQL
    /// handles visibility for all satellite nodes (threads, activities, etc.) under the user.
    /// </summary>
    private class UserScopeGrantHandler(ISecurityService securityService) : INodePostCreationHandler
    {
        public string NodeType => UserNodeType.NodeType;

        public async Task HandleAsync(MeshNode createdNode, string? createdBy, CancellationToken ct)
        {
            // Grant the user Viewer role on their own User node path.
            // This materializes into user_effective_permissions as Read on User/{userId}/...
            // so all satellite nodes (threads, activities) are visible to the user.
            var userId = createdNode.Id;
            if (string.IsNullOrEmpty(userId))
                return;

            var userPath = createdNode.Path ?? $"User/{userId}";
            await securityService.AddUserRoleAsync(userId, Role.Viewer.Id, userPath, assignedBy: "system", ct);
        }
    }
}
