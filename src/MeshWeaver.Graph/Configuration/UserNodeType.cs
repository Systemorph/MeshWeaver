using MeshWeaver.Layout;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
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
            services.AddSingleton<INodeTypeAccessRule>(sp =>
                new UserAccessRule(sp.GetRequiredService<IMessageHub>()));
            services.AddSingleton<INodePostCreationHandler>(sp =>
                new UserScopeGrantHandler(sp.GetRequiredService<IMeshService>()));
            return services;
        });
        builder.ConfigureNodeTypeAccess(a => a.WithPublicRead(NodeType));
        // nodeType:User without a path constraint → restrict to the "Auth"
        // partition (no fan-out needed). The "Auth" partition (formerly "User",
        // renamed in V27 / DefaultPartitionProvider) mirrors User / Group /
        // Role / VUser / ApiToken rows from every source partition via the
        // auth-mirror trigger, so a single-partition query covers every User
        // node in the mesh.
        //
        // Skip the override when the query targets a specific path
        // (e.g. ACME/User/Oliver, sample-data layouts that load users from
        // their source partition) — otherwise the Auth restriction would
        // hijack legitimate per-partition reads. The mirror is a discovery
        // index; queries that already know the path should follow the
        // natural first-segment partition route.
        builder.AddQueryRoutingRule(query =>
            query.ExtractNodeType() == NodeType && string.IsNullOrEmpty(query.Path)
                ? new QueryRoutingHints { Partition = "Auth" }
                : null);
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
        ExcludeFromContext = new HashSet<string> { "search" },
        // Post-v10 design: User nodes live at the ROOT namespace (path={userId}),
        // each user gets their own per-user partition. The previous design parked
        // them under namespace="User" — now superseded; setting an empty default
        // namespace + a single-element restriction list pinned to "" enforces the
        // root placement at create time, so runtime onboarding writes cannot land
        // a user node under "User/" by accident.
        Content = new NodeTypeDefinition { DefaultNamespace = "", RestrictedToNamespaces = [""], OwnsPartition = true },
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
    /// Grants public read access ONLY on the User node itself (path == "{userId}"),
    /// not on its children (threads, activities, etc.). Children inherit normal
    /// access control — the user gets read access via UserScopeGrantHandler.
    /// Post-v10 paths are root-level ({userId}); legacy "User/{userId}" still
    /// matches so transitional data does not lose visibility before migration.
    /// </summary>
    private static MessageHubConfiguration WithUserNodePublicRead(this MessageHubConfiguration config)
        => config.AddAccessRule(
            [NodeOperation.Read],
            (context, userId) =>
            {
                if (string.IsNullOrEmpty(userId)) return false;
                var nodePath = context.Node.Path;
                if (string.IsNullOrEmpty(nodePath)) return false;
                // Root-level user node ("Alice") with no namespace separator: public.
                if (!nodePath.Contains('/'))
                    return true;
                // Legacy path "User/Alice" (single segment under "User/"): public.
                return nodePath.StartsWith("User/", StringComparison.OrdinalIgnoreCase)
                       && !nodePath["User/".Length..].Contains('/');
            })
        .AddHubPermissionRule(
            Permission.Read,
            (_, userId) => !string.IsNullOrEmpty(userId));

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
    private class UserAccessRule(IMessageHub hub) : INodeTypeAccessRule
    {
        public string NodeType => UserNodeType.NodeType;

        public IReadOnlyCollection<NodeOperation> SupportedOperations =>
            [NodeOperation.Create, NodeOperation.Read, NodeOperation.Update];

        public IObservable<bool> HasAccess(NodeValidationContext context, string? userId)
        {
            if (context.Operation == NodeOperation.Read)
            {
                var nodePath = context.Node.Path;
                if (string.IsNullOrEmpty(nodePath))
                    return Observable.Return(false);
                // Root-level user node ({userId}) — readable by any authenticated user.
                if (!nodePath.Contains('/'))
                    return Observable.Return(!string.IsNullOrEmpty(userId));
                // Legacy "User" namespace passthrough (transitional).
                if (nodePath.Equals("User", StringComparison.OrdinalIgnoreCase))
                    return Observable.Return(true);
                if (nodePath.StartsWith("User/", StringComparison.OrdinalIgnoreCase)
                    && !nodePath["User/".Length..].Contains('/'))
                    return Observable.Return(!string.IsNullOrEmpty(userId));
                if (string.IsNullOrEmpty(userId))
                    return Observable.Return(false);
                return hub.CheckPermission(nodePath, userId, Permission.Read);
            }

            if (string.IsNullOrEmpty(userId))
                return Observable.Return(false);

            if (context.Operation == NodeOperation.Update)
            {
                var nodePath = context.Node.Path;
                if (!string.IsNullOrEmpty(nodePath))
                {
                    // Post-v10: user's partition is `{userId}` (root-level), not `User/{userId}`.
                    if (nodePath.Equals(userId, StringComparison.OrdinalIgnoreCase)
                        || nodePath.StartsWith(userId + "/", StringComparison.OrdinalIgnoreCase))
                        return Observable.Return(true);
                    // Legacy "User/{userId}" prefix — keep the rule honouring this
                    // shape until all in-flight data is migrated to root namespace.
                    var legacyPrefix = "User/" + userId;
                    if (nodePath.Equals(legacyPrefix, StringComparison.OrdinalIgnoreCase)
                        || nodePath.StartsWith(legacyPrefix + "/", StringComparison.OrdinalIgnoreCase))
                        return Observable.Return(true);
                }
            }

            return Observable.Return(IsPortalIdentity(userId));
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
        // Post-v10: per-user partition at root, so hubPath == userId. Strip
        // the legacy "User/" prefix when present so transitional addresses
        // continue to resolve until all data is migrated.
        var nodeOwnerId = hubPath.StartsWith("User/", StringComparison.OrdinalIgnoreCase)
            ? hubPath["User/".Length..]
            : hubPath;
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var viewerId = accessService?.Context?.ObjectId
                       ?? accessService?.CircuitContext?.ObjectId;

        if (string.IsNullOrEmpty(viewerId)
            || !string.Equals(viewerId, nodeOwnerId, StringComparison.OrdinalIgnoreCase))
            yield break;

        // Check if the user has Admin permissions at root level. Bridge the IObservable to
        // an IAsyncEnumerable via a Channel — no .ToTask(), no await on a hub round-trip.
        // See Doc/Architecture/AsynchronousCalls.md.
        var permsChannel = System.Threading.Channels.Channel.CreateBounded<Permission>(
            new System.Threading.Channels.BoundedChannelOptions(1) { FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest });

        // Match the platform-admin model: admin lives at the "Admin" scope
        // (AccessAssignment namespace Admin/_Access → scope Admin). Filter for the
        // POSITIVE All grant with a bounded wait — NOT FirstAsync, which captures the
        // premature empty static seed emitted before the synced AccessAssignment query
        // lands (same bug class as AdminMenuGate). A non-admin never emits a positive,
        // so the timeout fires and the tab stays hidden.
        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var sub = host.Hub.GetEffectivePermissions("Admin", viewerId)
            .Where(perm => perm.HasFlag(Permission.All))
            .Take(1)
            .Subscribe(
                perm => { permsChannel.Writer.TryWrite(perm); permsChannel.Writer.TryComplete(); },
                _ => permsChannel.Writer.TryComplete(),
                () => permsChannel.Writer.TryComplete());

        Permission rootPerms = Permission.None;
        try
        {
            await foreach (var perm in permsChannel.Reader.ReadAllAsync(cts.Token))
            {
                rootPerms = perm;
                break;
            }
        }
        catch (OperationCanceledException) { }

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
            .WithHiddenQuery("namespace:Admin/_Access nodeType:AccessAssignment")
            .WithShowSearchBox(false)
            .WithShowEmptyMessage(true)
            .WithRenderMode(MeshSearchRenderMode.Flat)
            .WithCollapsibleSections(false)
            .WithSectionCounts(false)
            .WithItemArea(MeshNodeLayoutAreas.ThumbnailArea)
            .WithDisableNavigation()
            .WithReactiveMode(true)
            .WithMaxColumns(2));

        // + Add Admin — reuse the Access Control area's Subject/Role picker dialog,
        // scoped to the "Admin" space so a new grant lands at
        // Admin/_Access/{subject}_Access (MainNode = "Admin"), the platform-admin shape.
        stack = stack.WithView(Controls.Button("+ Add Admin")
            .WithAppearance(Appearance.Accent)
            .WithStyle("align-self: flex-start; margin-top: 8px;")
            .WithClickAction((Action<UiActionContext>)(addCtx =>
                AccessControlLayoutArea.ShowAddAssignmentDialog(addCtx, "Admin"))));

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
    /// Post-creation handler that grants the user Admin access on their own User/{userId} scope.
    /// Materialized into user_effective_permissions so the standard access control SQL
    /// handles visibility for all satellite nodes (threads, activities, etc.) under the user.
    /// </summary>
    private class UserScopeGrantHandler(IMeshService meshService) : INodePostCreationHandler
    {
        public string NodeType => UserNodeType.NodeType;

        public Task HandleAsync(MeshNode createdNode, string? createdBy, CancellationToken ct)
        {
            // Grant the user Admin role on their own User/{userId} scope by
            // creating the AccessAssignment node directly via the mesh service.
            // No SecurityService.AddUserRole — that surface was removed; mutations
            // ride the standard data layer.
            var userId = createdNode.Id;
            if (string.IsNullOrEmpty(userId))
                return Task.CompletedTask;

            // Post-v10: User nodes live at the root namespace, so the user's
            // self-scope path is just {userId}. Fall back to the explicit Id
            // when Path is somehow unset rather than reverting to the legacy
            // "User/{userId}" shape — that would seed AccessAssignments at the
            // wrong scope and the user would have no permissions on their
            // actual partition.
            var userPath = !string.IsNullOrEmpty(createdNode.Path) ? createdNode.Path : userId;
            var assignmentNode = new MeshNode($"{userId}_Access", $"{userPath}/_Access")
            {
                NodeType = "AccessAssignment",
                Name = $"{userId} Access",
                MainNode = userPath,
                Content = new AccessAssignment
                {
                    AccessObject = userId,
                    DisplayName = userId,
                    Roles = System.Collections.Immutable.ImmutableList<RoleAssignment>.Empty
                        .Add(new RoleAssignment { Role = Role.Admin.Id }),
                },
            };

            // Fire-and-forget Subscribe — actor model serialises the per-node
            // hub's writes; we don't need to await before returning.
            meshService.CreateNode(assignmentNode).Subscribe(
                _ => { },
                _ => { /* error logging happens at the data layer */ });
            return Task.CompletedTask;
        }
    }
}
