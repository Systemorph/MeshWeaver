using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// The per-user <b>App</b> node type — the "installed app" record backing the home's Apps grid
/// (see <see cref="MeshWeaver.Mesh.App"/>). App nodes live at <c>{user}/_App/{appId}</c> as
/// REGULAR mesh nodes: deliberately NOT a satellite (no <c>IsSatelliteType</c>, no
/// <c>SatelliteTableMapping</c> entry — an unmapped <c>_App</c> segment routes to
/// <c>mesh_nodes</c>), and the <c>_</c> segment already keeps them out of the search context.
/// The home queries them with <c>path:{user}/_App scope:children nodeType:InstalledApp</c>.
/// <para>Writers: the Store's install flow creates the node when a viewer Gets/Adds an app;
/// removing the icon deletes the node (never the entitlement). The platform default apps are NOT
/// written as nodes — they come from <c>Admin/HomeConfig.DefaultApps</c> at render time.</para>
/// </summary>
public static class AppNodeType
{
    /// <summary>
    /// The NodeType discriminator. 🚨 Deliberately NOT the obvious "App": a built-in NodeType's
    /// definition node claims the TOP-LEVEL PATH of its name (AddMeshNodes below), and "App"/"app"
    /// is a name real content actually uses — a static claim there refuses node creation at
    /// <c>app</c>, breaks path resolution for <c>app/…</c>, and REFUSES installing any package
    /// named App (the static/durable claim collision, MeshWeaver#1209). "InstalledApp" keeps the
    /// path claim collision-improbable; the SEGMENT stays <c>_App</c>.
    /// </summary>
    public const string NodeType = "InstalledApp";

    /// <summary>The per-user namespace segment holding the app records (<c>_App</c>, a non-satellite dotfile).</summary>
    public const string UserNamespace = "_App";

    /// <summary>The path of one installed-app record: <c>{user}/_App/{appId}</c>.</summary>
    public static string PathFor(string user, string appId) => $"{user}/{UserNamespace}/{appId}";

    /// <summary>Registers the built-in installed-app node type + typed content on the mesh builder.</summary>
    public static TBuilder AddAppType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        builder.ConfigureHub(config => config.WithType<App>(nameof(App)));
        return builder;
    }

    /// <summary>MeshNode definition for <c>nodeType:InstalledApp</c>. No per-record layout area:
    /// the home's Apps grid paints its icon tiles straight from the query rows
    /// (<c>MeshSearchRenderMode.Icons</c>) — a per-record tile area meant one hub activation PER
    /// RESULT, the exact load the record model exists to avoid.</summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Installed App",
        Icon = "/static/NodeTypeIcons/puzzlepiece.svg",
        IsSatelliteType = false,
        ExcludeFromContext = new HashSet<string> { "search", "create", "content" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<App>())
    };
}
