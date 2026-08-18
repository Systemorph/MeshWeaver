using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides configuration for the GlobalSettings node type.
/// A single node of this type at path <c>_Setting</c> hosts the platform-wide settings page.
/// </summary>
public static class GlobalSettingsNodeType
{
    /// <summary>
    /// The NodeType value used to identify the global settings node.
    /// </summary>
    public const string NodeType = "GlobalSettings";

    /// <summary>
    /// Well-known path for the global settings node.
    /// </summary>
    public const string SettingsPath = "_Setting";

    /// <summary>
    /// Application href of the global settings page itself. Use this — never a literal — for any
    /// in-app link to Settings.
    ///
    /// <para>🚨 The registered path is <c>_Setting</c> — SINGULAR, capital S. Four call sites
    /// independently hand-wrote the plural lowercase form (<c>_settings</c>, with a leading slash)
    /// and every one of them 404'd with <i>"does not match any registered address pattern"</i>,
    /// which is what made the About and What's New pages unreachable from the profile menu (#1817).
    /// That form is not merely a typo to accept as an alias: lowercase <c>_settings</c> is a
    /// reserved satellite/schema segment in the cross-schema Postgres/Snowflake routing, an
    /// unrelated meaning. <c>GlobalSettingsRouteLiteralGuard</c> fails the build on a new one — and
    /// it is why this paragraph names the bad spelling without its leading slash.</para>
    /// </summary>
    public const string SettingsHref = "/" + SettingsPath;

    /// <summary>
    /// Application href of ONE global-settings tab — <c>/_Setting/GlobalSettings/{tabId}</c> — built
    /// through <see cref="LayoutAreaReference.ToHref(object)"/> so the URL shape comes from the same
    /// place the settings menu's own links come from and the two cannot drift.
    /// </summary>
    /// <param name="tabId">
    /// The tab's id, which is the settings menu entry's node id (<c>About</c>, <c>WhatsNew</c>,
    /// <c>ApiTokens</c>, …). Tab ids are declared one layer up (Memex.Portal.Shared), so this method
    /// takes the id rather than enumerating them.
    /// </param>
    public static string TabHref(string tabId) =>
        "/" + new LayoutAreaReference(GlobalSettingsLayoutArea.GlobalSettingsArea) { Id = tabId }
            .ToHref(SettingsPath);

    /// <summary>
    /// Registers the built-in "GlobalSettings" MeshNode on the mesh builder
    /// and creates the singleton _Setting node.
    /// </summary>
    public static TBuilder AddGlobalSettingsType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);

        // Create the well-known _Setting node
        builder.AddMeshNodes(new MeshNode(SettingsPath)
        {
            NodeType = NodeType,
            Name = "Settings",
            State = MeshNodeState.Active,
            ExcludeFromContext = new HashSet<string> { "search", "create" },
        });

        builder.AddMeshNodes(CreatePolicy());

        return builder;
    }

    /// <summary>
    /// The partition policy for <c>_Setting</c>: readable by everyone, writable by nobody.
    ///
    /// <para>🚨 <c>_Setting</c> is a TOP-LEVEL node, so it is its own partition, and a partition
    /// with no policy is private. The page it hosts is the opposite of private — About and What's
    /// New are ungated ("visible to every user"), and the header build chip links every signed-in
    /// user straight here. Without this an ordinary user reaching the page is refused at the
    /// partition: <i>"Access denied: user 'x' lacks Read permission on '_Setting'"</i>.</para>
    ///
    /// <para>This is the SECOND half of #1817 and it was invisible until the first half landed:
    /// while every link 404'd on the wrong path nobody ever reached the node to be denied by it,
    /// so fixing the route turned "does not match any registered address pattern" into an
    /// access denial on a live portal. Same class as #126, where the <c>Skill</c> partition
    /// shipped without its PublicRead policy and platform skills were invisible after
    /// deployment.</para>
    ///
    /// <para>Read-only is the whole grant: the write verbs stay <c>false</c>, and each tab still
    /// carries its own <c>RequiredPermission</c>, so opening the shell is not authority over what
    /// the admin tabs contain. Modelled on <c>LicenseNodeType</c>'s policy, which is world-readable
    /// for the same reason — you must be able to read the page before you can act on it.</para>
    /// </summary>
    private static MeshNode CreatePolicy() =>
        new("_Policy", SettingsPath)
        {
            NodeType = PartitionAccessPolicyNodeType.NodeType,
            Name = "Access Policy",
            Content = new PartitionAccessPolicy
            {
                PublicRead = true,
                Create = false,
                Update = false,
                Delete = false,
                Comment = false,
                Thread = false
            }
        };

    /// <summary>
    /// Creates a MeshNode definition for the GlobalSettings node type.
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Global Settings",
        Icon = "/static/NodeTypeIcons/settings.svg",
        IsSatelliteType = false,
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        HubConfiguration = config => config
            .AddDefaultGlobalSettingsMenuItems()
            .AddLayout(layout => layout
                .WithDefaultArea(GlobalSettingsLayoutArea.GlobalSettingsArea)
                .WithView(GlobalSettingsLayoutArea.GlobalSettingsArea, GlobalSettingsLayoutArea.GlobalSettings))
    };
}
