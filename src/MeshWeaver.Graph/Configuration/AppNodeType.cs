using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
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

    /// <summary>
    /// The per-record area the Notifications settings tab renders for each installed app (iOS
    /// <i>Settings → Notifications → {app}</i>): the app's card and the person's
    /// <see cref="NotificationAppPreference"/> editor. The tab lists the apps with a data-bound
    /// <c>MeshSearch</c> and hands each row to this area (<c>WithItemArea</c>), so the tab itself
    /// reads nothing on its hub — it renders at once and the list binds (Doc/GUI/DataBinding,
    /// "Templates first, data later"). Opt-in per surface: the home's Apps grid still paints its
    /// icon tiles from the query rows and activates no record hub.
    /// </summary>
    public const string NotificationPreferencesArea = "NotificationPreferences";

    /// <summary>
    /// The person and the app an installed-app record belongs to, read off its path
    /// <c>{user}/_App/{appId}</c>. False for any other shape, or an id that cannot key a
    /// notification preference (<see cref="NotificationApps.IsValidKey"/>).
    /// </summary>
    /// <param name="path">The installed-app record's path.</param>
    /// <param name="user">The owning person.</param>
    /// <param name="appId">The app id.</param>
    /// <returns>Whether the path is an installed-app record with a valid app key.</returns>
    public static bool TryParsePath(string? path, out string user, out string appId)
    {
        user = appId = string.Empty;
        var marker = $"/{UserNamespace}/";
        var at = path?.IndexOf(marker, StringComparison.Ordinal) ?? -1;
        if (path is null || at <= 0)
            return false;
        var id = path[(at + marker.Length)..];
        if (!NotificationApps.IsValidKey(id))
            return false;
        user = path[..at];
        appId = id;
        return true;
    }

    /// <summary>
    /// The area's control for one installed app, once its preference node exists: the app's card
    /// (bound to the record, so name and icon follow it) above the standard node-content editor
    /// bound straight to the person's preference node — every switch writes through the node
    /// stream; no /data replica, no save subscription.
    /// </summary>
    /// <param name="appPath">The installed-app record's path.</param>
    /// <param name="preferencePath">The person's preference node for that app.</param>
    /// <returns>The control.</returns>
    public static UiControl NotificationPreferencesFor(string appPath, string preferencePath)
        => Controls.Stack
            .WithView(MeshNodeThumbnailControl.ForPath(appPath))
            .WithView(MeshNodeContentEditorControl.ForType(preferencePath, typeof(NotificationAppPreference)));

    /// <summary>
    /// <see cref="NotificationPreferencesArea"/>: shows the loading line at once, creates the
    /// person's preference node on first view with the default the dispatcher already applies
    /// (<see cref="NotificationAppPreferenceNodeType.EnsureExists"/> — so opening the tab changes no
    /// delivery), then binds the editor to it. A refused seed is SHOWN, never swallowed.
    /// </summary>
    /// <param name="host">The record hub's area host.</param>
    /// <param name="_">The rendering context.</param>
    /// <returns>The area's controls.</returns>
    public static IObservable<UiControl?> NotificationPreferences(LayoutAreaHost host, RenderingContext _)
    {
        var appPath = host.Hub.Address.ToString();
        if (!TryParsePath(appPath, out var user, out var appId))
            return Observable.Return<UiControl?>(Controls.Markdown(host.Localize("ui.mdNotifPrefsUnavailable")));
        return NotificationAppPreferenceNodeType.EnsureExists(host.Hub, user, appId)
            .Select(preferencePath => (UiControl?)NotificationPreferencesFor(appPath, preferencePath))
            .Catch((Exception _) => Observable.Return<UiControl?>(
                Controls.Markdown(host.Localize("ui.mdNotifPrefsUnavailable"))))
            .StartWith((UiControl?)Controls.Markdown(host.Localize("ui.mdLoadingNotifPrefs")));
    }

    /// <summary>Registers the built-in installed-app node type + typed content on the mesh builder.</summary>
    public static TBuilder AddAppType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        builder.ConfigureHub(config => config.WithType<App>(nameof(App)));
        return builder;
    }

    /// <summary>MeshNode definition for <c>nodeType:InstalledApp</c>. No per-record TILE area: the
    /// home's Apps grid paints its icon tiles straight from the query rows
    /// (<c>MeshSearchRenderMode.Icons</c>) — a per-record tile area meant one hub activation PER
    /// RESULT, the exact load the record model exists to avoid. The one record area is
    /// <see cref="NotificationPreferencesArea"/>, which only the Notifications settings tab asks
    /// for, one row per app the person installed.</summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Installed App",
        Icon = "/static/NodeTypeIcons/puzzlepiece.svg",
        IsSatelliteType = false,
        ExcludeFromContext = new HashSet<string> { "search", "create", "content" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<App>())
            .AddLayout(layout => layout.WithView(NotificationPreferencesArea, NotificationPreferences))
    };
}
