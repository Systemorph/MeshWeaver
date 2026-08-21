using MeshWeaver.Data;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// The per-user <b>App</b> node type — the "installed app" record backing the home's Apps grid
/// (see <see cref="MeshWeaver.Mesh.App"/>). App nodes live at <c>{user}/_App/{appId}</c> as
/// REGULAR mesh nodes: deliberately NOT a satellite (no <c>IsSatelliteType</c>, no
/// <c>SatelliteTableMapping</c> entry — an unmapped <c>_App</c> segment routes to
/// <c>mesh_nodes</c>), and the <c>_</c> segment already keeps them out of the search context.
/// The home queries them with <c>path:{user}/_App scope:children nodeType:App</c>.
/// <para>Writers: the Store's install flow creates the node when a viewer Gets/Adds an app;
/// removing the icon deletes the node (never the entitlement). The platform default apps are NOT
/// written as nodes — they come from <c>Admin/HomeConfig.DefaultApps</c> at render time.</para>
/// </summary>
public static class AppNodeType
{
    /// <summary>The NodeType discriminator.</summary>
    public const string NodeType = "App";

    /// <summary>The per-user namespace segment holding the app records (<c>_App</c>, a non-satellite dotfile).</summary>
    public const string UserNamespace = "_App";

    /// <summary>The path of one installed-app record: <c>{user}/_App/{appId}</c>.</summary>
    public static string PathFor(string user, string appId) => $"{user}/{UserNamespace}/{appId}";

    /// <summary>Registers the built-in "App" node type + typed content on the mesh builder.</summary>
    public static TBuilder AddAppType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        builder.ConfigureHub(config => config.WithType<App>(nameof(App)));
        return builder;
    }

    /// <summary>MeshNode definition for <c>nodeType:App</c>.</summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "App",
        Icon = "/static/NodeTypeIcons/puzzlepiece.svg",
        IsSatelliteType = false,
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<App>())
    };
}
