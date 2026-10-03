using MeshWeaver.Layout;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
            // 🚨 NO per-type partition teardown here any more (#3436). Deleting a User home still
            // removes the ENTIRE per-user partition (backing store included) —
            // PartitionDropPostDeletionHandler now matches every partition ROOT structurally and
            // is registered ONCE by AddGraph. This registration was added by hand after the
            // 2026-07-19 memex-cloud incident left a whole user partition behind; copying the
            // Space registration instead of generalising it is what let the SAME defect recur for
            // Store/Plugin-rooted partitions. Interactive callers are still blocked upstream by
            // PartitionRootDeletionGuard — only System reaches the teardown for a User home.
            return services;
        });
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
        // 🚨 A NodeType DECLARATION declares itself a NodeType — never the type it declares.
        // This node used to carry `NodeType = "User"`, i.e. it claimed to BE a user, and every
        // `nodeType:User` query in the mesh therefore returned it alongside the real accounts.
        // It has no email, so the portal's user DIRECTORY (UserIdentityCache, whose whole job is
        // email → mesh user) tried to read it as a `User` and logged
        // `As<User> for User: value is NodeTypeDefinition` on EVERY index snapshot — 355k+
        // occurrences in production (Systemorph/MeshWeaver#2160/#2161/#2162). The same collision
        // made AI-source installs resolve a partition literally named "User" (`42P01: relation
        // "user.mesh_nodes" does not exist`) and, for the VUser twin, left a stray `vuser`
        // partition behind. Declaration nodes are identified mesh-wide by
        // `NodeType == MeshNode.NodeTypePath` (CreatableTypesProvider, MeshNodeLayoutAreas'
        // `-nodeType:NodeType` exclusions) — say so, exactly as Space/Release/Build do.
        NodeType = MeshNode.NodeTypePath,
        ExcludeFromContext = new HashSet<string> { "search", "content" },
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
            // The PERSON APP: Profile, Account, Preferences, Sharing (+ the personal tabs modules
            // register through PersonApp.AddPersonAppTab); node management hidden.
            .AddPersonAppTabs()
            // Platform administration moved to the Admin app; an old link redirects there.
            .RelocateSettingsTabsToAdminApp(GlobalAdministrationTab.TabId)
            .ApplyNodeHubContributions(NodeType)
            .AddLayout(layout => layout
                .WithDefaultArea(UserActivityLayoutAreas.ActivityArea)
                .WithView(MeshNodeLayoutAreas.SettingsArea, OwnSettings)
                // The Inbox app — what needs you, what is running, what just finished.
                .WithView(InboxLayoutArea.AreaName, InboxLayoutArea.Render))
    };

    /// <summary>
    /// The catalog keys for the tab's explanatory prose, in render order — one paragraph per
    /// picker. Exposed (rather than inlined) so the localization guard can assert they ARE keys:
    /// the time-zone paragraph shipped as an English literal while the language one beside it was
    /// localized from the start, which is precisely the shape that survives review.
    /// </summary>
    internal static readonly ImmutableList<string> PreferencesDescriptionKeys =
        ImmutableList.Create("settings.timeZoneDescription", "settings.languageDescription");

    /// <summary>
    /// The tab's two pickers, with every label resolved through <paramref name="localize"/>.
    ///
    /// <para>🚨 The localizer is a parameter for the same reason
    /// <c>PlatformUpdateChip.Describe</c>'s is: it makes the WORDING assertable without a hub, a
    /// circuit or a rendered layout area. That matters here because the defect this seam exists to
    /// prevent is invisible to every other gate — a hard-coded label is not a MISSING catalog key,
    /// so <c>LocalizationTest</c>'s completeness check passes, and a render test run under an
    /// English viewer sees the correct text either way.</para>
    ///
    /// <para>🚨 Every label added here MUST be a key. The time-zone label read
    /// "Display time zone (IANA)" to a German viewer for a month, sitting directly beside a
    /// language picker that had gone through the catalog since the day it was written.</para>
    /// </summary>
    internal static ImmutableList<MeshNodeEditorField> PreferenceFields(Func<string, string> localize)
        => ImmutableList.Create(
            new MeshNodeEditorField(
                nameof(User.TimeZoneId).ToCamelCase()!,
                localize("settings.timeZone"),
                MeshNodeEditorFieldKind.Enum)
            {
                Options = TimeZonePreference.SystemZoneIds()
            },
            new MeshNodeEditorField(
                nameof(User.Locale).ToCamelCase()!,
                localize("settings.language"),
                MeshNodeEditorFieldKind.Enum)
            {
                // Stores the BCP-47 tag ("de") but shows the endonym ("Deutsch") — a German
                // speaker looks for "Deutsch", not "German" or a raw tag.
                Options = Locales.Supported,
                OptionLabels = Locales.DisplayNames
            });

    /// <summary>
    /// Grants read access to every SIGNED-IN user on the User node itself (path == "{userId}"),
    /// not on its children (threads, activities, etc.). Children inherit normal
    /// access control — the user gets read access via UserScopeGrantHandler.
    /// Post-v10 paths are root-level ({userId}); legacy "User/{userId}" still
    /// matches so transitional data does not lose visibility before migration.
    ///
    /// <para>🚨 "Public" here has always meant <b>every authenticated user</b>, never "the
    /// anonymous internet". The gate is <see cref="WellKnownUsers.IsAuthenticated"/> — spelled
    /// <c>!string.IsNullOrEmpty(userId)</c>, it admitted the caller literally named
    /// <c>"Anonymous"</c>, and since the hub-permission rule below applies to EVERY
    /// <c>[RequiresPermission(Read)]</c> message reaching a user-partition hub, that one predicate
    /// served logged-out callers the partition's content collection over
    /// <c>/api/content/{user}/…</c>. An anonymous caller is not shut out by this: it falls through
    /// to the real evaluator, where an explicit Anonymous grant still allows the read.</para>
    /// </summary>
    private static MessageHubConfiguration WithUserNodePublicRead(this MessageHubConfiguration config)
        => config.AddAccessRule(
            [NodeOperation.Read],
            (context, userId) =>
            {
                if (!WellKnownUsers.IsAuthenticated(userId)) return false;
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
            (_, userId) => WellKnownUsers.IsAuthenticated(userId));

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

    /// <summary>
    /// The person app — <c>/{user}/Settings</c>, <see cref="PersonApp"/> — for the person it belongs
    /// to, and for nobody else. A User node is public-read (<c>WithUserNodePublicRead</c>), so without this
    /// gate any signed-in viewer could open another person's settings page and see every tab that
    /// demands no permission of its own. Platform administration is not here at all: it lives in
    /// the Admin app (<see cref="AdminAppNodeType"/>).
    /// </summary>
    private static IObservable<UiControl?> OwnSettings(LayoutAreaHost host, RenderingContext ctx)
    {
        var owner = OwnerOf(host.Hub.Address.ToString());
        var viewer = host.Hub.ServiceProvider.GetService<AccessService>().ViewerId();
        return string.IsNullOrEmpty(viewer) || !string.Equals(viewer, owner, StringComparison.OrdinalIgnoreCase)
            ? Observable.Return<UiControl?>(Controls.Markdown(host.Localize("settings.ownSettingsOnly")))
            : SettingsLayoutArea.Settings(host, ctx);
    }

    /// <summary>The user id a user hub belongs to — post-v10 the hub path IS the id; the legacy
    /// <c>User/</c> prefix is stripped.</summary>
    internal static string OwnerOf(string hubPath)
        => hubPath.StartsWith("User/", StringComparison.OrdinalIgnoreCase) ? hubPath["User/".Length..] : hubPath;

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

        public IObservable<System.Reactive.Unit> Handle(MeshNode createdNode, string? createdBy)
        {
            // Grant the user Admin role on their own User/{userId} scope by
            // creating the AccessAssignment node directly via the mesh service.
            // No SecurityService.AddUserRole — that surface was removed; mutations
            // ride the standard data layer.
            var userId = createdNode.Id;
            if (string.IsNullOrEmpty(userId))
                return Observable.Empty<System.Reactive.Unit>();

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

            // Reactive: return the create observable; the caller subscribes (the actor model
            // serialises the per-node hub's writes). Map to Unit for the handler contract.
            return meshService.CreateNode(assignmentNode).Select(_ => System.Reactive.Unit.Default);
        }
    }
}
