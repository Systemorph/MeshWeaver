using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Apps;

/// <summary>One app a viewer can see — what the launcher paints for it.</summary>
/// <param name="Id">The app's identity: its root path, or a built-in's id (<c>Settings</c>, <c>Admin</c>, <c>Inbox</c>).</param>
/// <param name="Name">The display name, from the app root.</param>
/// <param name="Icon">The icon, from the app root.</param>
/// <param name="Description">The one-line description, from the app root.</param>
/// <param name="Category">The default launcher group — the root's <see cref="MeshNode.Category"/>.</param>
/// <param name="OpenPath">Where the tile opens — the app's entry point (or the root).</param>
/// <param name="LabelKey">A localization key for a built-in's label; null for package apps.</param>
/// <param name="BuiltIn">Whether this is a platform entry rather than an app root.</param>
public sealed record AppDirectoryEntry(
    string Id,
    string Name,
    string Icon,
    string? Description,
    string? Category,
    string OpenPath,
    string? LabelKey = null,
    bool BuiltIn = false);

/// <summary>An app ROOT as read off the instance: a partition root whose content says <c>app: true</c>.</summary>
/// <param name="Path">The root's path — the app id.</param>
/// <param name="Name">The root's name.</param>
/// <param name="Icon">The root's icon.</param>
/// <param name="Description">The root's description.</param>
/// <param name="Category">The root's category.</param>
/// <param name="EntryPoint">The content's <c>entryPoint</c>, when declared.</param>
public sealed record AppRoot(
    string Path, string Name, string Icon, string? Description, string? Category, string? EntryPoint)
{
    /// <summary>Where the tile opens: the entry point, else the root.</summary>
    public string OpenPath => string.IsNullOrWhiteSpace(EntryPoint) ? Path : EntryPoint!.Trim('/');

    /// <summary>
    /// The NODE the visibility check reads. The ENTRY POINT, never the root: a gated root is
    /// readable by everyone as its storefront, so probing it would show every app to everyone.
    /// An entry point that addresses a layout area (<c>X/area/Y</c>) is probed at its node.
    /// </summary>
    public string ProbePath
    {
        get
        {
            var open = OpenPath;
            var area = open.IndexOf("/area/", StringComparison.Ordinal);
            return area > 0 ? open[..area] : open;
        }
    }
}

/// <summary>
/// The <b>app directory</b>: the apps a viewer can see, computed from the instance's app roots and
/// the access check — no per-user records. Design of record: MeshWeaver.Plugins
/// <c>Store/AppsOnTheInstance</c> §3–§4.
/// <list type="number">
/// <item><b>The app roots</b> — ONE process-wide query (<see cref="RootsQuery"/>), shared by every
/// viewer through <see cref="IMeshNodeStreamCache"/> and read as System, so every root is seen
/// whatever the viewer may read.</item>
/// <item><b>Visible</b> — per root, Read on its <see cref="AppRoot.ProbePath"/> for the viewer.
/// The check is the live security fold, so a grant or a revoke re-emits.</item>
/// <item><b>Built-ins</b> — Settings and Inbox for everyone, Administration for a global admin.</item>
/// </list>
/// Display (name, icon, description, category) comes straight from the root. Core knows nothing of
/// the Store's content types: the root's content is read by property name, in whatever shape it
/// arrives.
/// </summary>
public sealed class AppDirectory
{
    /// <summary>The stable id of the process-wide app-roots query.</summary>
    public const string RootsQueryId = "$app-roots";

    /// <summary>
    /// Every package ROOT whose content declares <c>app: true</c>. The established package-root
    /// shape: <c>nodeType:(Space OR Store/Plugin)</c>, because a partition root is CREATED as a
    /// <c>Space</c> and only its content is retyped on import, so a mirror may still index it as
    /// <c>Space</c> (a bare <c>nodeType:Store/Plugin</c> silently returns nothing there).
    /// Mesh-wide by nature and therefore declared (<c>partitions:all</c>), with the explicit
    /// limit <c>limit:all</c> (an ENUMERATION — a truncated page would silently drop an app from
    /// every launcher). <see cref="ReadRoot"/> re-checks the flag and the root shape in code.
    /// </summary>
    public static readonly string RootsQuery = SecurityQueries.Enumeration(
        $"namespace: nodeType:(Space OR Store/Plugin) is:main content.app:true {ParsedQuery.CrossPartitionQualifier}");

    /// <summary>The Settings built-in's id (the person app at <c>/{viewer}/Settings</c>).</summary>
    public const string SettingsId = "Settings";

    /// <summary>The Inbox built-in's id (the Inbox area on the viewer's own hub).</summary>
    public const string InboxId = "Inbox";

    /// <summary>The Administration built-in's id — the Admin app, global admins only.</summary>
    public const string AdminId = AdminAppNodeType.Path;

    /// <summary>The icon the Settings and Administration built-ins carry.</summary>
    public const string SettingsIcon = "/static/NodeTypeIcons/settings.svg";

    private readonly IMessageHub hub;
    private readonly ILogger<AppDirectory>? logger;

    /// <summary>Creates the directory over the mesh hub.</summary>
    public AppDirectory(IMessageHub hub)
    {
        this.hub = hub;
        logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger<AppDirectory>();
    }

    /// <summary>The app roots on the instance — live, shared process-wide.</summary>
    public IObservable<ImmutableList<AppRoot>> Roots()
    {
        // Resolved ONCE, on the caller's thread — never inside a long-lived selector (#2679).
        var cache = hub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        var options = hub.JsonSerializerOptions;
        return cache.GetQuery(RootsQueryId, options, RootsQuery)
            .Select(nodes => ReadRoots(nodes, options))
            .DistinctUntilChanged(RootListComparer.Instance);
    }

    /// <summary>
    /// The viewer's visible apps — live: a new root, a grant, a revoke or an admin change
    /// re-emits. Built-ins first, then the app roots by name. Not cached here; the launcher reads
    /// it through <see cref="AppDirectoryCache"/>.
    /// </summary>
    public IObservable<ImmutableList<AppDirectoryEntry>> Compute(string viewer)
    {
        var visible = Roots()
            .Select(roots => VisibleRoots(roots, viewer))
            .Switch();
        var admin = hub.IsGlobalAdmin(viewer)
            .Catch<bool, Exception>(exception =>
            {
                logger?.LogWarning(exception, "[AppDirectory] admin check failed for {Viewer}", viewer);
                return Observable.Return(false);
            })
            .DistinctUntilChanged();
        return visible
            .CombineLatest(admin, (apps, isAdmin) => Compose(viewer, apps, isAdmin))
            .DistinctUntilChanged(EntryListComparer.Instance);
    }

    private IObservable<ImmutableList<AppRoot>> VisibleRoots(ImmutableList<AppRoot> roots, string viewer)
    {
        if (roots.Count == 0)
            return Observable.Return(ImmutableList<AppRoot>.Empty);
        var checks = roots.Select(root => hub.CheckPermissionOutcome(root.ProbePath, viewer, Permission.Read)
            .Select(outcome =>
            {
                // A fold that reached NO verdict is not a denial — but a launcher can only show or
                // hide a tile, so the tile stays off for now and the reason is logged by name; the
                // live check re-emits once the fold answers.
                if (outcome.IsUndetermined)
                    logger?.LogWarning(
                        "[AppDirectory] read check on {Probe} for {Viewer} reached no verdict: {Reason}",
                        root.ProbePath, viewer, outcome.UndeterminedReason);
                return outcome.IsGranted;
            })
            .DistinctUntilChanged()
            .Select(allowed => (root, allowed)));
        return Observable.CombineLatest(checks)
            .Select(results => results.Where(r => r.allowed).Select(r => r.root).ToImmutableList());
    }

    /// <summary>
    /// The viewer's list from the visible roots and the admin flag: Settings, Inbox, and — for a
    /// global admin — Administration, then the apps by name. A root that IS a built-in's target
    /// (the <c>Admin</c> root) never appears twice. Pure.
    /// </summary>
    public static ImmutableList<AppDirectoryEntry> Compose(
        string viewer, IEnumerable<AppRoot> visibleRoots, bool isGlobalAdmin)
    {
        var builtIns = BuiltIns(viewer, isGlobalAdmin);
        var builtInIds = builtIns.Select(b => b.Id).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        var apps = visibleRoots
            .Where(r => !builtInIds.Contains(r.Path))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .Select(r => new AppDirectoryEntry(r.Path, r.Name, r.Icon, r.Description, r.Category, r.OpenPath));
        return builtIns.AddRange(apps);
    }

    /// <summary>
    /// The platform entries every viewer has — not records, so nothing to seed: <b>Settings</b>
    /// (the person app, <c>{viewer}/Settings</c>), <b>Inbox</b> (the Inbox area on the viewer's own
    /// hub, the target <c>SeedInboxAppLogonAction</c> seeds), and <b>Administration</b>
    /// (<c>Admin</c>) for a global admin only. Names are catalog keys, resolved per viewer. Pure.
    /// </summary>
    public static ImmutableList<AppDirectoryEntry> BuiltIns(string viewer, bool isGlobalAdmin)
    {
        var list = ImmutableList.Create(
            new AppDirectoryEntry(SettingsId, "Settings", SettingsIcon, null, null,
                $"{viewer}/{MeshNodeLayoutAreas.SettingsArea}", LabelKey: "common.settings", BuiltIn: true),
            new AppDirectoryEntry(InboxId, "Inbox", UserActivityLayoutAreas.InboxIcon, null, null,
                $"{viewer}/{InboxLayoutArea.AreaName}", LabelKey: UserActivityLayoutAreas.InboxLabelKey, BuiltIn: true));
        return isGlobalAdmin
            ? list.Add(new AppDirectoryEntry(AdminId, "Administration", SettingsIcon, null, null,
                AdminAppNodeType.Path, LabelKey: "adminApp.title", BuiltIn: true))
            : list;
    }

    /// <summary>
    /// The app roots among <paramref name="nodes"/>: partition roots (no namespace) whose content
    /// carries <c>app: true</c>, read SHAPE-TOLERANTLY — the content may be typed, a JsonElement or
    /// a JsonObject, and core does not reference the Store's content type. Pure.
    /// </summary>
    public static ImmutableList<AppRoot> ReadRoots(IEnumerable<MeshNode> nodes, JsonSerializerOptions options) =>
        nodes
            .Select(node => ReadRoot(node, options))
            .OfType<AppRoot>()
            .GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .ToImmutableList();

    /// <summary>One node as an app root, or null when it is not one. Pure.</summary>
    public static AppRoot? ReadRoot(MeshNode node, JsonSerializerOptions options)
    {
        if (!string.IsNullOrEmpty(node.Namespace) || string.IsNullOrEmpty(node.Path))
            return null;
        if (ElementOf(node, options) is not { } content)
            return null;
        if (!(Property(content, "app") is { ValueKind: JsonValueKind.True }))
            return null;
        var entryPoint = Property(content, "entryPoint") is { ValueKind: JsonValueKind.String } e
            ? e.GetString()
            : null;
        return new AppRoot(
            node.Path,
            string.IsNullOrWhiteSpace(node.Name) ? node.Path : node.Name!,
            string.IsNullOrWhiteSpace(node.Icon) ? UserActivityLayoutAreas.GenericAppIcon : node.Icon!,
            node.Description,
            node.Category,
            string.IsNullOrWhiteSpace(entryPoint) ? null : entryPoint);
    }

    /// <summary>The content as a JSON object, whatever shape it arrived in. Typed content is
    /// serialized with its CONCRETE runtime type.</summary>
    private static JsonElement? ElementOf(MeshNode node, JsonSerializerOptions options)
    {
        var element = node.Content switch
        {
            null => (JsonElement?)null,
            JsonElement je => je,
            System.Text.Json.Nodes.JsonNode jn => JsonSerializer.SerializeToElement(jn, options),
            var typed => JsonSerializer.SerializeToElement(typed, typed.GetType(), options),
        };
        return element is { ValueKind: JsonValueKind.Object } ? element : null;
    }

    /// <summary>A property by name, case-insensitively (camelCase on the wire, PascalCase typed).</summary>
    private static JsonElement? Property(JsonElement content, string name)
    {
        foreach (var property in content.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        return null;
    }

    private sealed class RootListComparer : IEqualityComparer<ImmutableList<AppRoot>>
    {
        public static readonly RootListComparer Instance = new();
        public bool Equals(ImmutableList<AppRoot>? x, ImmutableList<AppRoot>? y) =>
            ReferenceEquals(x, y) || (x is not null && y is not null && x.SequenceEqual(y));
        public int GetHashCode(ImmutableList<AppRoot> obj) => obj.Count;
    }

    private sealed class EntryListComparer : IEqualityComparer<ImmutableList<AppDirectoryEntry>>
    {
        public static readonly EntryListComparer Instance = new();
        public bool Equals(ImmutableList<AppDirectoryEntry>? x, ImmutableList<AppDirectoryEntry>? y) =>
            ReferenceEquals(x, y) || (x is not null && y is not null && x.SequenceEqual(y));
        public int GetHashCode(ImmutableList<AppDirectoryEntry> obj) => obj.Count;
    }
}
