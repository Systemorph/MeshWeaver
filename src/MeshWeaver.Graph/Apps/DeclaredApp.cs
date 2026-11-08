using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Apps;

/// <summary>
/// An app a MODULE declares for the launcher, beside the app roots — for a surface the module
/// serves that is not a package root, so no <c>app: true</c> root can stand for it: the platform
/// documentation (<c>AddDocumentation</c> declares <c>Doc</c>) and the viewer's Threads on an
/// instance that runs the AI engine without its package (the device mesh). The module that SERVES
/// the target declares the tile, so the tile exists exactly where its target does; a host that
/// does not register the module has neither (Doc/Architecture/AppsHome → "Declared apps").
/// <para>An app ROOT always wins over a declared app it covers: a visible root whose path is the
/// declared <see cref="Id"/>, or which opens the same <see cref="OpenPath"/>, replaces the declared
/// tile. Sharing the id with the root it stands in for (<c>AI</c> for Threads) keeps the viewer's
/// arrangement — group, order, hidden — on one key whichever of the two is shown.</para>
/// </summary>
/// <param name="Id">The tile's identity and the viewer's arrangement key — the app root it stands
/// in for, when there is one (<c>AI</c>), else the target's own path (<c>Doc</c>).</param>
/// <param name="Name">The display name when no <see cref="LabelKey"/> resolves.</param>
/// <param name="Icon">The tile icon.</param>
/// <param name="OpenPath">Where the tile opens. <see cref="ViewerToken"/> stands for the viewer's
/// own partition (<c>{user}/Chat</c>).</param>
/// <remarks>A SET modules contribute to, read through <see cref="InstalledModules.CurrentSet{T}"/>
/// (<see cref="AbsentWhenModuleGoneAttribute"/>): a declaration whose module is withdrawn at boot or
/// retired is absent — never a forwarder that throws and takes every viewer's launcher down.</remarks>
[AbsentWhenModuleGone]
public sealed record DeclaredApp(string Id, string Name, string Icon, string OpenPath)
{
    /// <summary>The placeholder for the viewer's own partition in <see cref="OpenPath"/> and
    /// <see cref="ProbePath"/> — the same token <c>hostedIn</c> uses for the person app.</summary>
    public const string ViewerToken = "{user}";

    /// <summary>A localization catalog key for the label, resolved per viewer; null = <see cref="Name"/>.</summary>
    public string? LabelKey { get; init; }

    /// <summary>The one-line description.</summary>
    public string? Description { get; init; }

    /// <summary>The launcher group, like a root's <c>launcherGroup</c>: <c>""</c> = a TOP-LEVEL tile,
    /// null = no group declared.</summary>
    public string? LauncherGroup { get; init; }

    /// <summary>Where the tile's group sorts among the groups (<see cref="App.GroupOrder"/>); 0 = none.</summary>
    public int LauncherGroupOrder { get; init; }

    /// <summary>
    /// The node the viewer must be able to READ for the tile to show — the same check an app root
    /// gets on its entry point. Null = no check: the target is the viewer's own partition, which
    /// they can always read. <see cref="ViewerToken"/> is resolved.
    /// </summary>
    public string? ProbePath { get; init; }

    /// <summary>Whether the tile is shown to a global admin only.</summary>
    public bool AdminOnly { get; init; }

    /// <summary>The target for <paramref name="viewer"/>. Pure.</summary>
    public string OpenPathFor(string viewer) => Resolve(OpenPath, viewer);

    /// <summary>The read probe for <paramref name="viewer"/>, or null when there is none. Pure.</summary>
    public string? ProbePathFor(string viewer) => ProbePath is null ? null : Resolve(ProbePath, viewer);

    private static string Resolve(string path, string viewer) =>
        path.Replace(ViewerToken, viewer, StringComparison.OrdinalIgnoreCase).Trim('/');
}

/// <summary>
/// A module's declaration that an app is the CATALOG of some node types — the code form of an app
/// root's <c>catalogs</c>, for the module that SERVES those types when the app browsing them is no
/// package root of its own (the built-in Inbox browses mail; the Threads app on a device mesh that
/// runs the AI engine without its package). While the module is installed, the types get no section
/// of their own on the home below the Apps band (Doc/Architecture/AppsHome → "Type sections").
/// </summary>
/// <param name="AppId">The app that catalogs the types — an app root's path or a built-in's id
/// (<c>Inbox</c>). Informational: the exclusion holds while the declaring module is installed.</param>
/// <param name="Types">The node types the app catalogs.</param>
/// <remarks>A SET modules contribute to, read absence-aware like <see cref="DeclaredApp"/>.</remarks>
[AbsentWhenModuleGone]
public sealed record AppCatalogDeclaration(string AppId, CatalogedTypeSet Types);

/// <summary>Registers <see cref="DeclaredApp"/>s on the mesh builder.</summary>
public static class DeclaredAppRegistration
{
    /// <summary>
    /// Declares <paramref name="app"/> for every viewer's launcher. Call it from the module that
    /// SERVES the app's target. A plain <c>AddSingleton</c>: declarations are an enumerable fan-in,
    /// read by <see cref="AppDirectory"/>; two declarations of one id keep the first.
    /// </summary>
    public static TBuilder AddDeclaredApp<TBuilder>(this TBuilder builder, DeclaredApp app) where TBuilder : MeshBuilder
    {
        builder.ConfigureServices(services => services.AddSingleton(app));
        return builder;
    }

    /// <summary>
    /// Declares that the app <paramref name="appId"/> is the CATALOG of <paramref name="types"/>
    /// (<see cref="AppCatalogDeclaration"/>): the home gives them no type section while this module
    /// is installed. Call it from the module that serves the types. A plain <c>AddSingleton</c>: an
    /// enumerable fan-in, read by <see cref="AppDirectory.CatalogedTypes"/>.
    /// </summary>
    public static TBuilder AddAppCatalog<TBuilder>(this TBuilder builder, string appId, params string[] types) where TBuilder : MeshBuilder
    {
        var declaration = new AppCatalogDeclaration(appId, CatalogedTypeSet.Of(types));
        builder.ConfigureServices(services => services.AddSingleton(declaration));
        return builder;
    }
}
