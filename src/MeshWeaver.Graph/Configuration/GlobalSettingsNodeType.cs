using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;

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

        return builder;
    }

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
