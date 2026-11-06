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
    bool BuiltIn = false)
{
    /// <summary>
    /// Whether <see cref="Category"/> was DECLARED as the launcher group (a package's
    /// <c>launcherGroup</c>, or the platform's own <c>Settings</c> group) rather than inherited from
    /// the root's store category. A declared group wins over a viewer's NON-custom placement — that
    /// placement is only the copy the legacy seed took — while a group the viewer chose themselves
    /// (<see cref="LauncherEntry.CustomGroup"/>) always wins. Init-only, so the constructor keeps its
    /// binary shape.
    /// </summary>
    public bool GroupDeclared { get; init; }

    /// <summary>Where the tile's group sorts among the groups (<see cref="App.GroupOrder"/>); 0 = no preference.</summary>
    public int GroupOrder { get; init; }

    /// <summary>The catalog key of the declared group's display name (<see cref="App.GroupLabelKey"/>); null = as written.</summary>
    public string? GroupLabelKey { get; init; }
}

/// <summary>What the person tile shows for the viewer: their display name, their picture (null =
/// the settings icon) and their language, for the description.</summary>
/// <param name="Name">The display name.</param>
/// <param name="Icon">The profile picture (the user node's icon), or null.</param>
/// <param name="Locale">The viewer's UI language, or null for the default.</param>
public sealed record PersonTile(string Name, string? Icon, string? Locale);

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
    /// <summary>The content's <c>hostedIn</c> — the host app the package is a section of
    /// (<c>Doc/Architecture/InAppExtensions</c>), when declared. An init property rather than a
    /// constructor parameter, so the record's constructor keeps its binary shape.</summary>
    public string? HostedIn { get; init; }

    /// <summary>
    /// The content's <c>launcherGroup</c>: the launcher group the tile sits in, overriding the
    /// store <see cref="Category"/>. <c>""</c> is a real value — a TOP-LEVEL tile, in no folder, like
    /// the Inbox — so null means "not declared" and nothing else. Data on the package root; core
    /// carries no list of which apps are top-level.
    /// </summary>
    public string? LauncherGroup { get; init; }

    /// <summary>The content's <c>launcherGroupOrder</c> — see <see cref="App.GroupOrder"/>; 0 = none.</summary>
    public int LauncherGroupOrder { get; init; }

    /// <summary>
    /// The content's <c>adminOnly</c>: the tile is shown to a GLOBAL ADMIN only, like the
    /// Administration built-in. A launcher-visibility switch and nothing else — it grants no read,
    /// takes none away, and leaves the package's skills, agents and node types working for every
    /// viewer (Feedback: everyone still files through <c>/feedback</c>, only the review app is an
    /// admin tile). Only a literal <c>true</c> opts in.
    /// </summary>
    public bool AdminOnly { get; init; }

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
/// What the instance says about which packages are installed — the inputs of
/// <see cref="AppDirectory.IsTile"/>.
/// </summary>
/// <param name="Records">The partitions with an install record (<c>Plugins/{id}</c>, keyed by its target partition).</param>
/// <param name="Synced">The partitions with a GitHub sync source (<c>{id}/_GitSync</c>).</param>
/// <param name="Uninstalled">The packages and partitions an uninstall request names whose phase 1 completed.</param>
public sealed record InstallEvidence(
    ImmutableHashSet<string> Records, ImmutableHashSet<string> Synced, ImmutableHashSet<string> Uninstalled)
{
    /// <summary>Nothing installed.</summary>
    public static readonly InstallEvidence None = new([], [], []);

    /// <summary>
    /// Whether the package <paramref name="id"/> is installed here: its install record exists — or,
    /// for a package provisioned by a git import (which writes no record), its sync source exists and
    /// no uninstall request that got past phase 1 names it. A package uninstalled with its data retained has lost its
    /// record (and, if git-provisioned, is named by its uninstall request), so it is not. Pure.
    /// </summary>
    public bool IsInstalled(string id) =>
        Records.Contains(id) || (Synced.Contains(id) && !Uninstalled.Contains(id));

    /// <inheritdoc />
    public bool Equals(InstallEvidence? other) =>
        other is not null && Records.SetEquals(other.Records) && Synced.SetEquals(other.Synced)
        && Uninstalled.SetEquals(other.Uninstalled);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Records.Count, Synced.Count, Uninstalled.Count);
}

/// <summary>
/// The <b>app directory</b>: the apps a viewer can see, computed from the instance's app roots and
/// the access check — no per-user records. Design of record: MeshWeaver.Plugins
/// <c>Store/AppsOnTheInstance</c> §3–§4.
/// <list type="number">
/// <item><b>The app roots</b> — ONE process-wide query (<see cref="RootsQuery"/>), shared by every
/// viewer through <see cref="IMeshNodeStreamCache"/> and read as System, so every root is seen
/// whatever the viewer may read.</item>
/// <item><b>Installed, and not a section of another app</b> — the root's package is installed on
/// THIS instance (<see cref="InstallEvidence.IsInstalled"/>: its <c>Plugins/{id}</c> record, or a
/// git-provisioned sync source no uninstall names) and declares no <c>hostedIn</c> other than the
/// person app (<see cref="IsTile"/>). A package uninstalled with its data retained keeps its root and
/// its <c>app: true</c>, so without this step it kept its tile.</item>
/// <item><b>Visible</b> — per root, Read on its <see cref="AppRoot.ProbePath"/> for the viewer.
/// The check is the live security fold, so a grant or a revoke re-emits.</item>
/// <item><b>Built-ins</b> — Settings and Inbox for everyone, Administration for a global admin.</item>
/// <item><b>Declared apps</b> — the tiles modules declare for what they serve (<see cref="DeclaredApp"/>:
/// Documentation, the Threads fallback), behind their own read probe, unless an app root covers them.</item>
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

    /// <summary>The stable id of the process-wide install-records query.</summary>
    public const string InstallRecordsQueryId = "$app-install-records";

    /// <summary>
    /// Every INSTALL RECORD on the instance — one <c>Package</c> node per installed package at
    /// <c>Plugins/{id}</c>, written by the platform's installer (registry, mount and provisioning
    /// installs alike) and removed by the package uninstall's phase 1. An enumeration: a truncated
    /// page would silently drop an installed app from every launcher.
    /// </summary>
    public static readonly string InstallRecordsQuery = SecurityQueries.Enumeration(
        $"path:{InstallRecordsPartition} scope:children nodeType:Package");

    /// <summary>The partition that holds the install records.</summary>
    public const string InstallRecordsPartition = "Plugins";

    /// <summary>The stable id of the process-wide sync-sources query.</summary>
    public const string SyncSourcesQueryId = "$app-sync-sources";

    /// <summary>
    /// Every GitHub sync source (<c>{partition}/_GitSync…</c>) — the evidence that a package
    /// provisioned by a git import is installed, for the provisioning path that writes no install
    /// record (a root created, its sync wired and imported). Mesh-wide by nature, so declared.
    /// </summary>
    public static readonly string SyncSourcesQuery = SecurityQueries.Enumeration(
        $"nodeType:GitHubSyncConfig {ParsedQuery.CrossPartitionQualifier}");

    /// <summary>The stable id of the process-wide uninstall-requests query.</summary>
    public const string UninstallRequestsQueryId = "$app-uninstall-requests";

    /// <summary>
    /// Every package-uninstall request (<see cref="PackageUninstallRequest.Namespace"/>, content
    /// <c>package</c> + <c>status</c>). Phase 1 removes the install record but keeps the data — the
    /// sync source included — so a request that got PAST phase 1 (<see cref="UninstalledStatuses"/>)
    /// naming a package with no record says it was uninstalled here.
    /// </summary>
    public static readonly string UninstallRequestsQuery = SecurityQueries.Enumeration(
        $"namespace:{PackageUninstallRequest.Namespace} nodeType:{PackageUninstallRequest.NodeType}");

    /// <summary>
    /// The request statuses that mean phase 1 completed — the package is uninstalled here, its data
    /// retained or being dropped. The same set the executor's own re-install block reads
    /// (<c>PackageUninstallExecutor.UninstalledHere</c>): a request still <c>Requested</c> or
    /// <c>Uninstalling</c> has not removed anything yet, and a <c>Failed</c> one — phase 1 refuses a
    /// package with no install record, a shared partition, user data — removed nothing at all.
    /// </summary>
    public static readonly ImmutableHashSet<string> UninstalledStatuses = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        PackageUninstallStatus.AwaitingConfirmation, PackageUninstallStatus.TearingDown, PackageUninstallStatus.Done);

    /// <summary>The <c>hostedIn</c> value naming the person app (<c>/{user}/Settings</c>). A package
    /// hosted THERE contributes a settings tab and may still be an app of its own; any other host
    /// makes the package a section of that app, never a tile.</summary>
    public const string PersonAppHost = "{user}/Settings";

    /// <summary>The Settings built-in's id (the person app at <c>/{viewer}/Settings</c>).</summary>
    public const string SettingsId = "Settings";

    /// <summary>
    /// The launcher group the platform's own settings tiles — the person app and Administration —
    /// sit in. One group, last: <see cref="LastGroupOrder"/>.
    /// </summary>
    public const string SettingsGroup = "Settings";

    /// <summary>The catalog key the <see cref="SettingsGroup"/> heading is localized by.</summary>
    public const string SettingsGroupLabelKey = "common.settings";

    /// <summary>
    /// The <see cref="App.GroupOrder"/> that sorts a group after every group with no preference.
    /// Large and fixed so a package declaring <c>launcherGroupOrder</c> below it stays above Settings.
    /// </summary>
    public const int LastGroupOrder = 1000;

    /// <summary>The Inbox built-in's id (the Inbox area on the viewer's own hub).</summary>
    public const string InboxId = "Inbox";

    /// <summary>The Administration built-in's id — the Admin app, global admins only.</summary>
    public const string AdminId = AdminAppNodeType.Path;

    /// <summary>The catalog key of the person tile's description — "your profile, accounts and
    /// connections: your settings".</summary>
    public const string PersonSettingsDescriptionKey = "launcher.personSettingsDescription";

    /// <summary>The icon the Settings and Administration built-ins carry.</summary>
    public const string SettingsIcon = "/static/NodeTypeIcons/settings.svg";

    private readonly IMessageHub hub;
    private readonly ILogger<AppDirectory>? logger;

    // When the module set changes, and what the modules declare now (DeclaredApp). Instance state.
    private readonly IObservable<long> moduleVersions;
    private readonly Func<ImmutableList<DeclaredApp>> readDeclarations;

    /// <summary>Creates the directory over the mesh hub.</summary>
    public AppDirectory(IMessageHub hub)
        : this(hub,
            hub.ServiceProvider.GetService<ModuleContexts>()?.VersionChanged ?? Observable.Never<long>(),
            () => ReadDeclarations(hub.ServiceProvider))
    {
    }

    /// <summary>
    /// The declarations as they stand NOW, ABSENCE-AWARE: through <see cref="InstalledModules.CurrentSet{T}"/>,
    /// so a declaration whose module was withdrawn at boot or retired is simply absent — never the
    /// root forwarder that can only throw, which made <c>GetServices</c> fail every viewer's whole
    /// directory (Settings and Inbox included) for good. A host without the module registry has no
    /// held modules, so its plain registrations are the set.
    /// </summary>
    internal static ImmutableList<DeclaredApp> ReadDeclarations(IServiceProvider services) =>
        services.GetService<InstalledModules>() is { } installed
            ? installed.CurrentSet<DeclaredApp>(services).ToImmutableList()
            : services.GetServices<DeclaredApp>().ToImmutableList();

    /// <summary>
    /// Seam constructor (unit tests via InternalsVisibleTo): <paramref name="moduleVersions"/> says
    /// the module set changed, and <paramref name="readDeclarations"/> reads the declarations as they
    /// stand THEN — production passes <see cref="ModuleContexts.VersionChanged"/> and
    /// <see cref="ReadDeclarations"/>, the ABSENCE-AWARE read through
    /// <see cref="InstalledModules.CurrentSet{T}"/>. Never pass a plain <c>GetServices&lt;DeclaredApp&gt;()</c>
    /// here: it throws for a withdrawn module's forwarder and takes every viewer's directory down.
    /// </summary>
    internal AppDirectory(IMessageHub hub, IObservable<long> moduleVersions, Func<ImmutableList<DeclaredApp>> readDeclarations)
    {
        this.hub = hub;
        this.moduleVersions = moduleVersions;
        this.readDeclarations = readDeclarations;
        logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger<AppDirectory>();
    }

    /// <summary>
    /// The modules' declarations — live: read now, and RE-READ whenever the module set changes
    /// (<see cref="ModuleContexts.VersionChanged"/>), so a live module replacement that changes a
    /// declaration's target, label or probe reaches a launcher that is already open; the warm cache
    /// keeps a computation alive for as long as anyone watches, so a snapshot taken once would serve
    /// the replaced generation's tile indefinitely. Deduplicated first-registration-wins
    /// (<see cref="FirstPerId"/>) BEFORE any probe or admin filter sees them.
    /// <para>The read is a service RESOLUTION inside a selector, deliberately: the declarations are a
    /// class-typed module service, which the root resolves to the current generation on every
    /// request — resolving once would pin the generation the launcher first saw.</para>
    /// </summary>
    public IObservable<ImmutableList<DeclaredApp>> Declarations() =>
        Observable.Create<ImmutableList<DeclaredApp>>(observer =>
            {
                // 🚨 SUBSCRIBE FIRST, then take the initial snapshot: VersionChanged is a plain,
                // non-replaying subject, so a swap between an initial read and the subscription would
                // be lost and an open launcher would keep the old tile for good.
                // Reads are SERIALIZED by a drain loop rather than a lock: one reader at a time, a
                // signal that arrives during a read (on any thread, or re-entrantly from the read
                // itself) makes the reader read AGAIN, so the last emission always comes from a read
                // that started after the last swap — and two reads never emit out of order.
                var pending = 0;
                void Signal()
                {
                    if (Interlocked.Increment(ref pending) != 1)
                        return;
                    do
                    {
                        Interlocked.Exchange(ref pending, 1);
                        observer.OnNext(FirstPerId(SafeRead()));
                    }
                    while (Interlocked.Decrement(ref pending) != 0);
                }
                var subscription = moduleVersions.Subscribe(_ => Signal(), observer.OnError);
                Signal();
                return subscription;
            })
            .DistinctUntilChanged(DeclarationListComparer.Instance);

    // A read that fails is "no declared tiles this time", logged — never a fault that would take the
    // built-ins (Settings, Inbox) down with it; the next module swap reads again.
    private ImmutableList<DeclaredApp> SafeRead()
    {
        try
        {
            return readDeclarations();
        }
        catch (Exception exception)
        {
            logger?.LogError(exception,
                "[AppDirectory] the declared apps could not be read; the launcher serves without them until the next module change");
            return ImmutableList<DeclaredApp>.Empty;
        }
    }

    /// <summary>One declaration per id, the FIRST registration winning — whatever its visibility, so a
    /// later declaration of the same id can never stand in for one the viewer may not see. Pure.</summary>
    public static ImmutableList<DeclaredApp> FirstPerId(IEnumerable<DeclaredApp> declared) =>
        declared
            .Where(d => !string.IsNullOrWhiteSpace(d.Id))
            .GroupBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToImmutableList();

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
    /// What the instance says is installed — live, shared process-wide and read as System: the
    /// install records, the sync sources and the uninstall requests (<see cref="InstallEvidence"/>).
    /// </summary>
    public IObservable<InstallEvidence> Installed()
    {
        // Resolved ONCE, on the caller's thread — never inside a long-lived selector (#2679).
        var cache = hub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        var options = hub.JsonSerializerOptions;
        var records = cache.GetQuery(InstallRecordsQueryId, options, InstallRecordsQuery)
            .Select(nodes => RecordIds(nodes, options));
        var synced = cache.GetQuery(SyncSourcesQueryId, options, SyncSourcesQuery)
            .Select(nodes => PartitionsOf(nodes));
        // CQRS: the listing names WHICH requests exist; each one's status is read from its own node
        // stream, because a listing's content can trail the write that moved the request on (the
        // same split PackageUninstallExecutor.UninstalledHere makes). A request seen in the listing
        // exists, so its stream answers; a fault reads as "no evidence from that request".
        var uninstalled = cache.GetQuery(UninstallRequestsQueryId, options, UninstallRequestsQuery)
            .Select(nodes => nodes.Select(n => n.Path).Where(p => !string.IsNullOrEmpty(p))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.Ordinal).ToImmutableList())
            .DistinctUntilChanged(PathListComparer.Instance)
            .Select(paths => paths.Count == 0
                ? Observable.Return(ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase))
                : Observable.CombineLatest(paths.Select(path => cache.GetStream(path, options)
                        .Select(node => (MeshNode?)node)
                        .Catch<MeshNode?, Exception>(exception =>
                        {
                            logger?.LogWarning(exception,
                                "[AppDirectory] uninstall request {Path} unreadable; it counts as no uninstall evidence", path);
                            return Observable.Return<MeshNode?>(null);
                        })))
                    .Select(requests => UninstalledPackages(requests.OfType<MeshNode>(), options)))
            .Switch();
        return records.CombineLatest(synced, uninstalled, (r, s, u) => new InstallEvidence(r, s, u))
            .DistinctUntilChanged();
    }

    /// <summary>
    /// The app roots that are TILES on this instance — live: a root that is installed here and is
    /// not a section of another app (<see cref="IsTile"/>). An uninstalled package whose data was
    /// retained keeps its root, and with it <c>app: true</c>; only the install evidence tells it
    /// apart, which is why the directory reads it.
    /// </summary>
    public IObservable<ImmutableList<AppRoot>> InstalledRoots() =>
        Roots()
            .CombineLatest(Installed(), (roots, installed) => roots.Where(r => IsTile(r, installed)).ToImmutableList())
            .DistinctUntilChanged(RootListComparer.Instance);

    /// <summary>
    /// Whether an app root is a launcher tile: it is INSTALLED here (<see cref="InstallEvidence.IsInstalled"/>)
    /// and it is not hosted in another app — a package declaring <c>hostedIn</c> is a section of its
    /// host (a tab of the Examples app, a game of the Games app) and gets no tile, except a package
    /// hosted in the person app (<see cref="PersonAppHost"/>), which only contributes a settings tab
    /// and may still be an app. Pure.
    /// </summary>
    public static bool IsTile(AppRoot root, InstallEvidence installed) =>
        !IsSectionOfAnotherApp(root) && installed.IsInstalled(root.Path);

    /// <summary>Whether the root declares a host other than the person app. Pure.</summary>
    public static bool IsSectionOfAnotherApp(AppRoot root) =>
        root.HostedIn is { Length: > 0 } host
        && !string.Equals(host.Trim(), PersonAppHost, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The partitions the install records say are installed — keyed like the app roots, by
    /// PARTITION: a record's <c>targetPartition</c> when it declares one, else its id (the package
    /// id, which is the partition by default — <c>PackageInstaller.TargetPartitionOf</c>). Content is
    /// read in whatever shape it arrives. Pure.
    /// </summary>
    public static ImmutableHashSet<string> RecordIds(IEnumerable<MeshNode> nodes, JsonSerializerOptions? options = null) =>
        nodes
            .Where(n => string.Equals(n.Namespace, InstallRecordsPartition, StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(n.Id))
            .Select(n => options is not null && ElementOf(n, options) is { } c
                         && Property(c, "targetPartition") is { ValueKind: JsonValueKind.String } t
                         && t.GetString() is { } target && !string.IsNullOrWhiteSpace(target)
                ? target.Trim().Trim('/')
                : n.Id)
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The partitions <paramref name="nodes"/> live in — their first path segment. Pure.</summary>
    public static ImmutableHashSet<string> PartitionsOf(IEnumerable<MeshNode> nodes) =>
        nodes
            .Select(n => (n.Path ?? "").Trim('/').Split('/', 2)[0])
            .Where(p => p.Length > 0)
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The packages the uninstall requests name whose phase 1 completed (content
    /// <c>package</c> and <c>status</c> ∈ <see cref="UninstalledStatuses"/>, read in whatever shape
    /// the content arrives). Pure.</summary>
    public static ImmutableHashSet<string> UninstalledPackages(IEnumerable<MeshNode> nodes, JsonSerializerOptions options) =>
        nodes
            .SelectMany(n => ElementOf(n, options) is { } c
                             && Property(c, "status") is { ValueKind: JsonValueKind.String } status
                             && UninstalledStatuses.Contains(status.GetString() ?? "")
                ? UninstalledNames(c)
                : [])
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The viewer's visible apps — live: a new root, a grant, a revoke or an admin change
    /// re-emits. Built-ins first, then the app roots by name. Not cached here; the launcher reads
    /// it through <see cref="AppDirectoryCache"/>.
    /// </summary>
    public IObservable<ImmutableList<AppDirectoryEntry>> Compute(string viewer)
    {
        var visible = InstalledRoots()
            .Select(roots => VisibleRoots(roots, viewer))
            .Switch();
        // 🚨 A FAULT is not a verdict: it propagates, so AppDirectoryCache evicts this computation
        // and the next subscriber recomputes — a Catch(false) here would cache "not an admin" and
        // complete the leg, so no later grant could ever reach it.
        var admin = hub.IsGlobalAdmin(viewer)
            .Do(_ => { }, exception => logger?.LogWarning(exception,
                "[AppDirectory] admin check failed for {Viewer}; the directory recomputes on the next subscriber", viewer))
            .DistinctUntilChanged();
        // The viewer's own node names the Settings tile (their name, their picture, their
        // language). It is COSMETIC: the list never waits for it (StartWith) and a failed read
        // falls back to the plain "Settings" tile rather than taking the launcher down.
        var person = PersonOf(viewer);
        // The apps modules DECLARE (DeclaredApp — the platform documentation, the Threads fallback),
        // each behind its own read probe — re-read on every module swap, and the probes SWITCHED to
        // the fresh declarations, so an open launcher follows a live module replacement.
        var declared = Declarations()
            .Select(apps => VisibleDeclared(apps, viewer))
            .Switch();
        return visible
            .CombineLatest(admin, person, declared,
                (apps, isAdmin, who, modules) => Compose(viewer, apps, isAdmin, who, modules))
            .DistinctUntilChanged(EntryListComparer.Instance);
    }

    /// <summary>
    /// The declared apps the viewer may see — live: a declaration with a <see cref="DeclaredApp.ProbePath"/>
    /// needs Read on it, exactly like an app root's entry point (an undetermined check FAILS the
    /// computation rather than latching the tile off); one without is the viewer's own surface.
    /// </summary>
    private IObservable<ImmutableList<DeclaredApp>> VisibleDeclared(ImmutableList<DeclaredApp> apps, string viewer)
    {
        if (apps.Count == 0)
            return Observable.Return(ImmutableList<DeclaredApp>.Empty);
        var checks = apps.Select(app => app.ProbePathFor(viewer) is { Length: > 0 } probe
            ? hub.CheckPermissionOutcome(probe, viewer, Permission.Read)
                .Select(outcome =>
                {
                    if (outcome.IsUndetermined)
                        throw new InvalidOperationException(
                            $"[AppDirectory] read check on declared app '{app.Id}' ('{probe}') for '{viewer}' reached no verdict: {outcome.UndeterminedReason}");
                    return outcome.IsGranted;
                })
                .DistinctUntilChanged()
                .Select(allowed => (app, allowed))
            : Observable.Return((app, allowed: true)));
        return Observable.CombineLatest(checks)
            .Select(results => results.Where(r => r.allowed).Select(r => r.app).ToImmutableList());
    }

    /// <summary>The viewer's own user node, read as <see cref="PersonTile"/> — live, never blocking:
    /// it starts with <c>null</c> (the plain Settings tile) and re-emits when the name, the picture
    /// or the language changes.
    /// <para>🚨 EXISTENCE first, through an OWNED, empty-on-absent query (<see cref="PersonQuery"/>,
    /// <see cref="IMeshQueryCore"/> — torn down with the viewer's directory slot, never a
    /// process-resident cached query), and the authoritative node stream only once the node is
    /// seen: the directory also serves System-backed <c>{owner}/_Apps</c> reads for arbitrary
    /// owners, and a point read of an owner with no node answers NotFound and opens the stream
    /// cache's storm breaker for that path. The same shape as
    /// <see cref="LauncherArrangementSource.Observe"/>.</para></summary>
    private IObservable<PersonTile?> PersonOf(string viewer)
    {
        if (string.IsNullOrWhiteSpace(viewer))
            return Observable.Return<PersonTile?>(null);
        // Resolved ONCE, on the caller's thread — never inside a long-lived selector (#2679).
        var core = hub.ServiceProvider.GetRequiredService<IMeshQueryCore>();
        var cache = hub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        var options = hub.JsonSerializerOptions;
        var request = MeshQueryRequest.FromQuery(PersonQuery(viewer)) with { UserId = WellKnownUsers.System };
        return LauncherArrangementSource.Fold(core.Query<MeshNode>(request, options))
            .Select(nodes => nodes.ContainsKey(viewer))
            .DistinctUntilChanged()
            .Select(exists => exists
                ? cache.GetStream(viewer, options).Select(node => ReadPerson(node, options))
                : Observable.Return<PersonTile?>(null))
            .Switch()
            .Catch<PersonTile?, Exception>(exception =>
            {
                logger?.LogDebug(exception,
                    "[AppDirectory] user node of {Viewer} unreadable; the Settings tile keeps its plain label", viewer);
                return Observable.Return<PersonTile?>(null);
            })
            .StartWith((PersonTile?)null)
            .DistinctUntilChanged();
    }

    /// <summary>The person-tile existence query: the viewer's own root, narrowed to the User type —
    /// empty when there is no such node, and never a Space or another root that happens to sit at
    /// an owner path the directory is asked about. Pure.</summary>
    public static string PersonQuery(string viewer) => $"path:{viewer} nodeType:{UserNodeType.NodeType}";

    /// <summary>The person tile's facts from a user node: the display name (the node's name, else
    /// <c>fullName</c>), the picture (the node's icon) and the language (<c>locale</c>). Null when
    /// the node carries no name at all. Pure.</summary>
    public static PersonTile? ReadPerson(MeshNode? node, JsonSerializerOptions options)
    {
        // Only a USER node names the person tile — a Space (or any other root) at the same path never does.
        if (node is null || !string.Equals(node.NodeType, UserNodeType.NodeType, StringComparison.Ordinal))
            return null;
        var content = ElementOf(node, options);
        string? Text(string name) => content is { } c && Property(c, name) is { ValueKind: JsonValueKind.String } v
            ? v.GetString()
            : null;
        var name = !string.IsNullOrWhiteSpace(node.Name) && !string.Equals(node.Name, node.Path, StringComparison.Ordinal)
            ? node.Name
            : Text("fullName");
        if (string.IsNullOrWhiteSpace(name))
            return null;
        // The picture is resolved HERE, against the user node: an uploaded one is stored relative
        // (`content:<file>`), and the tile is painted from a synthetic {viewer}/_Apps row, where a
        // relative value would resolve against the wrong node. The profile page's own classifier
        // (ResolvePictureUrl): an emoji, inline SVG or Fluent icon name is no picture (null → the settings icon).
        var picture = MeshNodeImageHelper.ResolvePictureUrl(node.Icon, node.Path);
        return new PersonTile(name.Trim(), string.IsNullOrWhiteSpace(picture) ? null : picture, Text("locale"));
    }

    private IObservable<ImmutableList<AppRoot>> VisibleRoots(ImmutableList<AppRoot> roots, string viewer)
    {
        if (roots.Count == 0)
            return Observable.Return(ImmutableList<AppRoot>.Empty);
        var checks = roots.Select(root => hub.CheckPermissionOutcome(root.ProbePath, viewer, Permission.Read)
            .Select(outcome =>
            {
                // A check that reached NO verdict is neither a grant nor a denial — and the outcome
                // stream that reports it has ended. Treating it as "hidden" would latch the tile off;
                // it FAILS the computation instead, so the cache evicts it and the next subscriber
                // recomputes against a fold that can answer.
                if (outcome.IsUndetermined)
                    throw new InvalidOperationException(
                        $"[AppDirectory] read check on '{root.ProbePath}' for '{viewer}' reached no verdict: {outcome.UndeterminedReason}");
                return outcome.IsGranted;
            })
            .DistinctUntilChanged()
            .Select(allowed => (root, allowed)));
        return Observable.CombineLatest(checks)
            .Select(results => results.Where(r => r.allowed).Select(r => r.root).ToImmutableList());
    }

    /// <summary>
    /// The viewer's list from the visible roots and the admin flag: Settings, Inbox, and — for a
    /// global admin — Administration, then the apps by name (an <see cref="AppRoot.AdminOnly"/>
    /// app for a global admin only). A root that IS a built-in's target
    /// (the <c>Admin</c> root) never appears twice. Pure.
    /// </summary>
    public static ImmutableList<AppDirectoryEntry> Compose(
        string viewer, IEnumerable<AppRoot> visibleRoots, bool isGlobalAdmin, PersonTile? person = null)
        => Compose(viewer, visibleRoots, isGlobalAdmin, person, declared: null);

    /// <summary>
    /// <see cref="Compose(string, IEnumerable{AppRoot}, bool, PersonTile?)"/> plus the apps modules
    /// DECLARE (<see cref="DeclaredApp"/>, already filtered to the ones the viewer may read): each is
    /// a tile among the apps, by name, UNLESS a visible app root covers it — the root's path is the
    /// declared id, or it opens the same target — or a built-in has its id or target. A root always
    /// wins, so a portal that shows the AI package's Threads tile never shows the engine's fallback
    /// beside it. Two declarations of one id keep the first. Pure.
    /// </summary>
    public static ImmutableList<AppDirectoryEntry> Compose(
        string viewer, IEnumerable<AppRoot> visibleRoots, bool isGlobalAdmin, PersonTile? person,
        IEnumerable<DeclaredApp>? declared)
    {
        var builtIns = BuiltIns(viewer, isGlobalAdmin, person);
        var builtInIds = builtIns.Select(b => b.Id).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        var roots = visibleRoots
            .Where(r => !builtInIds.Contains(r.Path))
            .Where(r => isGlobalAdmin || !r.AdminOnly)
            .ToImmutableList();
        // What a root or a built-in already puts on the launcher — two SEPARATE sets: a declaration's
        // id is compared only with existing ids, its target only with existing targets (a root `Handbook`
        // opening `Doc` must not hide a declaration `Doc` opening `Doc/Reference`).
        var coveredIds = roots.Select(r => r.Path)
            .Concat(builtIns.Select(b => b.Id))
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        var coveredTargets = roots.Select(r => r.OpenPath)
            .Concat(builtIns.Select(b => b.OpenPath))
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        var rootEntries = roots.Select(r => new AppDirectoryEntry(
            r.Path, r.Name, r.Icon, r.Description, r.LauncherGroup ?? r.Category, r.OpenPath)
        {
            GroupDeclared = r.LauncherGroup is not null,
            GroupOrder = r.LauncherGroupOrder,
        });
        // First registration per id wins BEFORE the admin filter, so an admin-only first declaration
        // is never replaced by a later unrestricted one of the same id.
        var declaredEntries = FirstPerId(declared ?? [])
            .Where(d => isGlobalAdmin || !d.AdminOnly)
            .Where(d => !coveredIds.Contains(d.Id) && !coveredTargets.Contains(d.OpenPathFor(viewer)))
            .Select(d => new AppDirectoryEntry(
                d.Id, d.Name, d.Icon, d.Description, d.LauncherGroup, d.OpenPathFor(viewer),
                LabelKey: d.LabelKey, BuiltIn: true)
            {
                GroupDeclared = d.LauncherGroup is not null,
                GroupOrder = d.LauncherGroupOrder,
            });
        var apps = rootEntries.Concat(declaredEntries)
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Id, StringComparer.OrdinalIgnoreCase);
        return builtIns.AddRange(apps);
    }

    /// <summary>
    /// The platform entries every viewer has — not records, so nothing to seed: <b>Settings</b>
    /// (the person app, <c>{viewer}/Settings</c>), <b>Inbox</b> (the Inbox area on the viewer's own
    /// hub, the target <c>SeedInboxAppLogonAction</c> seeds), and <b>Administration</b>
    /// (<c>Admin</c>) for a global admin only. Names are catalog keys, resolved per viewer. Pure.
    /// <para>The person app is the viewer's OWN app, so once their node is known
    /// (<paramref name="person"/>) its tile carries their name and picture — "Roland Bürgi", not
    /// "Settings" — and a description, in their language, that says it holds their settings
    /// (<see cref="PersonSettingsDescriptionKey"/>). Without it the tile is the plain catalog
    /// "Settings".</para>
    /// </summary>
    public static ImmutableList<AppDirectoryEntry> BuiltIns(string viewer, bool isGlobalAdmin, PersonTile? person = null)
    {
        var settingsPath = $"{viewer}/{MeshNodeLayoutAreas.SettingsArea}";
        var settings = person is { Name.Length: > 0 }
            ? new AppDirectoryEntry(SettingsId, person.Name, person.Icon ?? SettingsIcon,
                LocalizationCatalog.Get(PersonSettingsDescriptionKey, person.Locale), SettingsGroup,
                settingsPath, LabelKey: null, BuiltIn: true)
            : new AppDirectoryEntry(SettingsId, "Settings", SettingsIcon, null, SettingsGroup,
                settingsPath, LabelKey: "common.settings", BuiltIn: true);
        // The settings tiles sit in ONE group, last (Architecture/AppsHome); the Inbox stays top-level.
        settings = settings with { GroupDeclared = true, GroupOrder = LastGroupOrder, GroupLabelKey = SettingsGroupLabelKey };
        var list = ImmutableList.Create(
            settings,
            new AppDirectoryEntry(InboxId, "Inbox", UserActivityLayoutAreas.InboxIcon, null, null,
                $"{viewer}/{InboxLayoutArea.AreaName}", LabelKey: UserActivityLayoutAreas.InboxLabelKey, BuiltIn: true));
        return isGlobalAdmin
            ? list.Add(new AppDirectoryEntry(AdminId, "Admin", SettingsIcon, null, SettingsGroup,
                AdminAppNodeType.Path, LabelKey: "adminApp.title", BuiltIn: true)
            { GroupDeclared = true, GroupOrder = LastGroupOrder, GroupLabelKey = SettingsGroupLabelKey })
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
        var hostedIn = Property(content, "hostedIn") is { ValueKind: JsonValueKind.String } h
            ? h.GetString()
            : null;
        var launcherGroup = Property(content, "launcherGroup") is { ValueKind: JsonValueKind.String } g
            ? g.GetString()?.Trim()
            : null;
        var groupOrder = Property(content, "launcherGroupOrder") is { ValueKind: JsonValueKind.Number } o
            && o.TryGetInt32(out var parsedOrder)
            ? parsedOrder
            : 0;
        var adminOnly = Property(content, "adminOnly") is { ValueKind: JsonValueKind.True };
        return new AppRoot(
            node.Path,
            string.IsNullOrWhiteSpace(node.Name) ? node.Path : node.Name!,
            string.IsNullOrWhiteSpace(node.Icon) ? UserActivityLayoutAreas.GenericAppIcon : node.Icon!,
            node.Description,
            node.Category,
            string.IsNullOrWhiteSpace(entryPoint) ? null : entryPoint)
        {
            HostedIn = string.IsNullOrWhiteSpace(hostedIn) ? null : hostedIn.Trim(),
            LauncherGroup = launcherGroup,
            LauncherGroupOrder = groupOrder,
            AdminOnly = adminOnly,
        };
    }

    /// <summary>What one completed request says is gone: the package id and every partition it
    /// recorded (<c>partitions[].partition</c>) — roots and sync sources are keyed by partition.</summary>
    private static IEnumerable<string> UninstalledNames(JsonElement content)
    {
        if (Property(content, "package") is { ValueKind: JsonValueKind.String } p && p.GetString() is { } package)
            yield return package;
        if (Property(content, "partitions") is { ValueKind: JsonValueKind.Array } partitions)
            foreach (var entry in partitions.EnumerateArray())
                if (entry.ValueKind == JsonValueKind.Object
                    && Property(entry, "partition") is { ValueKind: JsonValueKind.String } part
                    && part.GetString() is { } name)
                    yield return name;
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

    private sealed class DeclarationListComparer : IEqualityComparer<ImmutableList<DeclaredApp>>
    {
        public static readonly DeclarationListComparer Instance = new();
        public bool Equals(ImmutableList<DeclaredApp>? x, ImmutableList<DeclaredApp>? y) =>
            ReferenceEquals(x, y) || (x is not null && y is not null && x.SequenceEqual(y));
        public int GetHashCode(ImmutableList<DeclaredApp> obj) => obj.Count;
    }

    private sealed class PathListComparer : IEqualityComparer<ImmutableList<string>>
    {
        public static readonly PathListComparer Instance = new();
        public bool Equals(ImmutableList<string>? x, ImmutableList<string>? y) =>
            ReferenceEquals(x, y) || (x is not null && y is not null && x.SequenceEqual(y, StringComparer.Ordinal));
        public int GetHashCode(ImmutableList<string> obj) => obj.Count;
    }

    private sealed class EntryListComparer : IEqualityComparer<ImmutableList<AppDirectoryEntry>>
    {
        public static readonly EntryListComparer Instance = new();
        public bool Equals(ImmutableList<AppDirectoryEntry>? x, ImmutableList<AppDirectoryEntry>? y) =>
            ReferenceEquals(x, y) || (x is not null && y is not null && x.SequenceEqual(y));
        public int GetHashCode(ImmutableList<AppDirectoryEntry> obj) => obj.Count;
    }
}
