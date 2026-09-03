using System.Collections.Immutable;
using System.ComponentModel;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// The catalog browse/install view: lists the installable packages a <see cref="IPackageSource"/>
/// offers at a git ref, shows each package's install status (comparing against the <c>Plugins</c>
/// install registry), and offers an Install / Update button that runs the install. Reactive end to
/// end — after an install the registry stream re-emits and the affected card flips to "Installed"
/// with no manual refresh.
///
/// <para><b>Categories first, packages per category.</b> The page a visitor lands on lists the
/// source's CATEGORIES — one tile per <see cref="PackageManifest.Category"/> with its package count,
/// plus an "all packages" entry — and reads nothing but the source's manifest listing to do so: no
/// install record, no admin probe, no activation state. Picking a tile (<c>?category=…</c>) renders
/// that category's cards and joins ONLY its members against the install registry (one exact-path
/// read per member); the whole flat list stays reachable behind <c>?all=true</c>, which is also the
/// one page that can show the install records the source no longer offers. "The store must not load
/// the full thing — only categories first" is the rule this shape implements; the pure seams
/// (<see cref="Plan"/>, <see cref="Categories"/>, <see cref="InstalledRecordQueries"/>) are what a
/// test pins it with.</para>
///
/// <para>The rendering is source-agnostic (<see cref="RenderFromSource"/>): the <c>PluginCatalog</c>
/// node Overview builds its source from the node's <see cref="PluginCatalogContent"/> and renders +
/// installs through the helpers here. (The old platform-admin "Plugin Catalog" settings tab that
/// also consumed this rendering was retired — browsing and provisioning is the Store's job; the
/// global-settings About tab shows the read-only installed inventory via
/// <see cref="ObserveInstalledManifests"/>.)</para>
/// </summary>
public static class CatalogLayoutAreas
{
    /// <summary>Area name for the catalog browse view.</summary>
    public const string CatalogArea = "Catalog";

    /// <summary>
    /// The area parameter that selects ONE category to browse (<c>?category=Education</c>); absent
    /// = the landing, which lists the categories and renders no package card at all.
    /// </summary>
    public const string CategoryParam = "category";

    /// <summary>
    /// The area parameter that asks for EVERY package on one page (<c>?all=true</c>) — the flat
    /// list the catalog used to open with, kept reachable behind an explicit click because it is
    /// the only page that can show the install records the source no longer offers
    /// (<see cref="Orphaned"/>). Anything but a true-ish value is not a request for it.
    /// </summary>
    public const string AllParam = "all";

    /// <summary>
    /// The bucket KEY for packages that declare no <see cref="PackageManifest.Category"/> — a key,
    /// never a label: the tile and the heading render it through <c>ui.catalogUncategorized</c>, in
    /// the viewer's language. A source category literally spelled this way joins the bucket, which
    /// means the same thing.
    /// </summary>
    public const string Uncategorized = "Uncategorized";

    /// <summary>
    /// The install-registry listing the ALL page joins against — every record, content included,
    /// because that page renders every card and the orphan section needs the records the source no
    /// longer offers.
    /// </summary>
    public const string AllInstalledQuery =
        $"path:{PackageInstaller.InstalledPartition} scope:children nodeType:{PackageInstaller.PackageNodeType}";

    /// <summary>
    /// The SHELL-only install-registry listing a category page reads for the click's dependency
    /// closure (<see cref="PackageDependencyGraph.InstallClosure"/> skips what is already installed):
    /// record paths, no <c>content</c>. An install record carries the package's whole installed-file
    /// baseline, which is exactly the payload a page rendering one category must not load for every
    /// package on the instance — <c>select:</c> is what keeps the column out of the read.
    /// </summary>
    public const string InstalledIdsQuery =
        $"{AllInstalledQuery} select:path,id,namespace,nodeType";

    /// <summary>
    /// Registers the <c>PluginCatalog</c> node views: the catalog browse as the default Overview,
    /// plus the standard create/delete areas.
    /// </summary>
    /// <param name="configuration">The message hub configuration to register on.</param>
    /// <returns>The configuration with the catalog views registered.</returns>
    public static MessageHubConfiguration AddPluginCatalogViews(this MessageHubConfiguration configuration)
        => configuration
            .AddDefaultLayoutAreas()
            .AddMeshDataSource(s => s.WithContentType<PluginCatalogContent>())
            .AddLayout(layout => layout
                .WithView(MeshNodeLayoutAreas.OverviewArea, Overview)
                .WithView(CatalogArea, Catalog));
            // Create / Delete are no longer re-registered here: their views ride the
            // MeshWeaver.Graph.Views module, which registers them on every per-node hub, so this
            // hub gets them without naming an implementation the platform no longer carries.

    /// <summary>The default Overview is the catalog.</summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext ctx) => Catalog(host, ctx);

    /// <summary>
    /// Renders the catalog for a <c>PluginCatalog</c> node: builds the source from the node's
    /// <see cref="PluginCatalogContent"/> and renders through <see cref="RenderFromSource"/>.
    /// </summary>
    /// <param name="host">The layout area host rendering the area.</param>
    /// <param name="_">The rendering context for the area.</param>
    /// <returns>An observable stream of the catalog view.</returns>
    [Browsable(false)]
    public static IObservable<UiControl?> Catalog(LayoutAreaHost host, RenderingContext _)
    {
        return host.Workspace.GetMeshNodeStream()
            .Select(node => node.ContentAs<PluginCatalogContent>(host.Hub.JsonSerializerOptions))
            .Select(cfg => RenderFromSource(
                host, BuildSource(host, cfg?.SourceRepoPath, cfg?.SourceSubdir),
                cfg?.SourceRef ?? "HEAD", cfg?.Description,
                cfg?.SourceRepoPath is { Length: > 0 } p ? $"{p} @ {cfg.SourceRef}" : null))
            .Switch()
            .StartWith((UiControl?)Controls.Markdown(host.Localize("ui.mdLoadingCatalog")));
    }

    // ————————————————————————————————————————————— the page plan (pure)

    /// <summary>Which of the catalog's three pages a render is.</summary>
    public enum CatalogPage
    {
        /// <summary>The category tiles — reads the manifest listing and nothing else.</summary>
        Landing,

        /// <summary>One category's cards, joined against ITS members' install records.</summary>
        Category,

        /// <summary>Every card plus the orphaned-record section — the whole install registry.</summary>
        All,
    }

    /// <summary>One category tile: the bucket key and how many packages it holds.</summary>
    /// <param name="Key">The category as the source spells it, or <see cref="Uncategorized"/>.</param>
    /// <param name="Count">How many available packages fall into it.</param>
    public sealed record CatalogCategory(string Key, int Count);

    /// <summary>
    /// What ONE render of the catalog shows, decided from the area reference and the source's
    /// listing alone — before any install record is read. <see cref="Packages"/> is the set of
    /// cards the page renders (empty on the landing), <see cref="Available"/> the whole listing,
    /// which every card's click still needs as the dependency-resolution universe.
    /// </summary>
    /// <param name="Kind">Which page.</param>
    /// <param name="Category">The selected category's key on a <see cref="CatalogPage.Category"/> page; else null.</param>
    /// <param name="Categories">The tiles, in display order.</param>
    /// <param name="Packages">The cards this page renders.</param>
    /// <param name="Available">Everything the source offers.</param>
    public sealed record CatalogPlan(
        CatalogPage Kind, string? Category, IReadOnlyList<CatalogCategory> Categories,
        IReadOnlyList<PackageManifest> Packages, IReadOnlyList<PackageManifest> Available)
    {
        /// <summary>How many packages the source offers in total.</summary>
        public int Total => Available.Count;
    }

    /// <summary>The category bucket a package falls into: its trimmed category, or
    /// <see cref="Uncategorized"/> when it declares none. Pure.</summary>
    public static string EffectiveCategory(PackageManifest package) =>
        string.IsNullOrWhiteSpace(package.Category) ? Uncategorized : package.Category!.Trim();

    /// <summary>Whether a bucket key is the <see cref="Uncategorized"/> bucket. Pure.</summary>
    public static bool IsUncategorized(string key) =>
        string.Equals(key, Uncategorized, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The category tiles for a listing: one per distinct category (matched case-insensitively,
    /// spelled as first seen), alphabetical, the uncategorized bucket last. Counted off the
    /// manifests alone — no package node is read to produce a count. Pure.
    /// </summary>
    public static IReadOnlyList<CatalogCategory> Categories(IEnumerable<PackageManifest> available) =>
        [.. (available ?? [])
            .GroupBy(EffectiveCategory, StringComparer.OrdinalIgnoreCase)
            .Select(g => new CatalogCategory(g.Key, g.Count()))
            .OrderBy(c => IsUncategorized(c.Key) ? 1 : 0)
            .ThenBy(c => c.Key, StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// The requested category matched case-insensitively to an ACTUAL category, or null (the
    /// landing) when the request is blank or names no category the source has — a stale or
    /// mistyped <c>?category=</c> falls back to the tiles rather than a blank page. Pure.
    /// </summary>
    public static string? SelectedCategory(string? requested, IEnumerable<CatalogCategory> categories)
    {
        if (string.IsNullOrWhiteSpace(requested))
            return null;
        var want = requested.Trim();
        return categories.FirstOrDefault(c => string.Equals(c.Key, want, StringComparison.OrdinalIgnoreCase))?.Key;
    }

    /// <summary>Whether the request asks for the whole flat list (<c>?all=true</c>). Pure.</summary>
    public static bool IsAll(string? requested) =>
        string.Equals(requested?.Trim(), "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(requested?.Trim(), "1", StringComparison.Ordinal);

    /// <summary>The packages of one category, by display name. Pure.</summary>
    public static IReadOnlyList<PackageManifest> InCategory(IEnumerable<PackageManifest> available, string category) =>
        [.. (available ?? [])
            .Where(p => string.Equals(EffectiveCategory(p), category, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Name ?? p.Id, StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// The page plan for a render: the ALL page when asked for, one category when the request
    /// names one the source has, otherwise the landing. The landing's <see cref="CatalogPlan.Packages"/>
    /// is EMPTY by construction — that is the statement "read no install record for this page".
    /// Pure.
    /// </summary>
    public static CatalogPlan Plan(
        string? requestedCategory, string? requestedAll, IReadOnlyList<PackageManifest> available)
    {
        available ??= [];
        var categories = Categories(available);
        if (IsAll(requestedAll))
            return new(CatalogPage.All, null, categories, available, available);
        if (SelectedCategory(requestedCategory, categories) is { } category)
            return new(CatalogPage.Category, category, categories, InCategory(available, category), available);
        return new(CatalogPage.Landing, null, categories, [], available);
    }

    /// <summary>
    /// The install-record reads a category page issues: one exact-path query per member, as one
    /// batched request — never the registry's whole children listing. Blanks dropped, duplicates
    /// collapsed, ordinally sorted so the same page always asks the same question. Each query is
    /// its own change-feed scope, so an install landing while the page is open still flips its
    /// card. Pure.
    /// </summary>
    public static IReadOnlyList<string> InstalledRecordQueries(IEnumerable<string> packageIds) =>
        [.. (packageIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .Select(id => $"path:{PackageInstaller.InstalledPartition}/{id} nodeType:{PackageInstaller.PackageNodeType}")];

    /// <summary>The href a category tile navigates to — the catalog area of the node at
    /// <paramref name="address"/>, carrying the URL-encoded category. Pure.</summary>
    public static string CategoryHref(object address, string category) =>
        new LayoutAreaReference(CatalogArea)
        {
            Id = $"{CatalogArea}?{CategoryParam}={Uri.EscapeDataString(category)}",
        }.ToHref(address);

    /// <summary>The href of the whole flat list. Pure.</summary>
    public static string AllHref(object address) =>
        new LayoutAreaReference(CatalogArea) { Id = $"{CatalogArea}?{AllParam}=true" }.ToHref(address);

    /// <summary>The href of the landing — the tiles. Pure.</summary>
    public static string LandingHref(object address) =>
        new LayoutAreaReference(CatalogArea).ToHref(address);

    // ————————————————————————————————————————————— the render

    /// <summary>
    /// Renders the catalog from an arbitrary <paramref name="source"/>. The source's listing
    /// decides the page (<see cref="Plan"/>): the landing renders straight off it; a card page joins
    /// its cards against the install registry (live), the viewer's admin flag (live) and the
    /// restart-as-activation state (live). Shared by the node Overview and every test of it.
    /// </summary>
    internal static IObservable<UiControl?> RenderFromSource(
        LayoutAreaHost host, IPackageSource? source, string sourceRef, string? description, string? sourceLabel)
    {
        // Which page THIS render is — known synchronously off the area reference, which is what
        // lets the landing skip every per-package read below.
        var requestedCategory = host.Reference?.GetParameterValue(CategoryParam);
        var requestedAll = host.Reference?.GetParameterValue(AllParam);
        return ObserveAvailable(host, source, sourceRef)
            .Select(feed =>
            {
                if (!feed.Answered)
                    return Observable.Return((UiControl?)Controls.Markdown(host.Localize("ui.mdLoadingCatalog")));
                var plan = Plan(requestedCategory, requestedAll, feed.Packages);
                if (plan.Kind == CatalogPage.Landing)
                    // The landing composes NOTHING beyond the listing it was built from: no install
                    // record, no permission evaluation, no activation-state read. That is the whole
                    // point of opening on categories.
                    return Observable.Return((UiControl?)BuildLanding(host, source, description, sourceLabel, plan));
                return ObserveInstalledFor(host, plan)
                    .CombineLatest(ObserveInstalledIds(host, plan), ObserveViewerIsGlobalAdmin(host),
                        ObserveActivation(host),
                        (installed, installedIds, isAdmin, activation) => (UiControl?)BuildPackages(
                            host, source, sourceRef, description, sourceLabel, plan, installed, installedIds,
                            isAdmin, activation));
            })
            // Switch, never SelectMany: a re-listing of the source supersedes the page built from
            // the previous listing instead of leaving two compositions pushing into one view.
            .Switch();
    }

    /// <summary>
    /// The restart-as-activation state of THIS process, as a live leg of the catalog render (#1979).
    ///
    /// <para><b>Why the catalog is where this belongs.</b> Loading a module is restart-as-activation
    /// by design, so the restart IS the last step of an install that declares one — and an install
    /// whose last step is invisible reads as a broken install: buy, "installed", the feature is not
    /// there, and nothing anywhere says why. This is the surface the person is looking at when the
    /// install completes, which is why the note goes on the package card rather than only on the
    /// operator health check that already reports the same report object.</para>
    ///
    /// <para><b>Why it needs the change signal and not a timer.</b> The module lands strictly AFTER
    /// the install record is written, so the record's own re-render arrives too early to see it.
    /// <see cref="ModuleLandingService.ActivationChanged"/> fires on the write itself — one emission
    /// per landing, nothing polled and nothing retried — and this leg re-derives the report from it.
    /// Every emission RE-READS: the state changes underneath a running process (that is the whole
    /// point), so a cached answer would be wrong exactly when it matters.</para>
    ///
    /// <para>The read is a small file read, and it runs on the shared FileSystem IO pool rather than
    /// the render thread or the landing service's own cap-1 pool — the latter would queue this read
    /// behind the very landing that announced it.</para>
    /// </summary>
    private static IObservable<ModuleActivationReport> ObserveActivation(LayoutAreaHost host)
    {
        var pending = host.Hub.ServiceProvider.GetService<PendingModuleActivations>();
        if (pending is null)
            // No module lane on this host at all (a mesh without the plugin catalog's services).
            // An EMPTY report, not an undetermined one: nothing here can ever be pending, which is
            // a known answer, and rendering "could not determine" would be a claim about a
            // mechanism this deployment does not have.
            return Observable.Return(new ModuleActivationReport([]));

        var pool = host.Hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.FileSystem)
                   ?? IoPool.Unbounded;
        var landing = host.Hub.ServiceProvider.GetService<ModuleLandingService>();
        var changed = landing?.ActivationChanged ?? Observable.Empty<System.Reactive.Unit>();

        return changed
            .StartWith(System.Reactive.Unit.Default)
            // Switch, not SelectMany: two landings in quick succession must not race their reads
            // into the view out of order. The newest read wins and an in-flight stale one is
            // dropped — which is right for a view whose only job is to show the CURRENT state.
            .Select(_ => pool.InvokeBlocking(ct => pending.Read())
                .Catch((Exception ex) =>
                {
                    // The reader already turns an unreadable record into an UNDETERMINED report; a
                    // throw here is the pool refusing the work (teardown). Reported as undetermined
                    // for the same reason — never as "nothing pending", which is the one answer that
                    // would silently promise the install finished.
                    Logger(host)?.LogWarning(ex, "Catalog: reading the module activation state failed.");
                    return Observable.Return(new ModuleActivationReport(
                        [], "the activation state could not be read on this host"));
                }))
            .Switch()
            // So the catalog renders immediately instead of waiting on a file read — never a
            // Take(1) anywhere here: this feeds a live data-bound view.
            .StartWith(new ModuleActivationReport([]));
    }

    /// <summary>
    /// The viewer's global-admin status as a LIVE flag for the catalog view: false until the
    /// permission evaluator positively confirms admin, then tracking it — the stream stays live and
    /// <c>DistinctUntilChanged</c>, so a later revocation (or a faulted-and-caught emission) flips
    /// it back to false and the view re-renders in the non-admin shape. That is the point: the flag
    /// follows the grant rather than latching. Never <c>Take(1)</c> on the first emission — the
    /// evaluator seeds a premature <c>false</c> before its <c>AccessAssignment</c> query lands,
    /// which would freeze an admin's view into the non-admin shape; and never <c>Take(1)</c> at
    /// all, because this feeds a live data-bound view.
    /// </summary>
    private static IObservable<bool> ObserveViewerIsGlobalAdmin(LayoutAreaHost host)
    {
        var viewerId = ResolveViewerId(host);
        if (string.IsNullOrEmpty(viewerId))
            return Observable.Return(false);
        return host.Hub.IsGlobalAdmin(viewerId!)
            .Catch<bool, Exception>(_ => Observable.Return(false))
            .StartWith(false)
            // After StartWith, so the evaluator's own seeded false does not re-render the view.
            .DistinctUntilChanged();
    }

    /// <summary>The signed-in viewer's id, or null when nobody is signed in.</summary>
    internal static string? ResolveViewerId(LayoutAreaHost host)
    {
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        return accessService?.Context?.ObjectId ?? accessService?.CircuitContext?.ObjectId;
    }

    // Live list of installable packages from the given source at its ref, carrying whether the
    // snapshot is the seed or a real ANSWER — the page frame paints on the seed, the empty message
    // waits for an answer (a listing failure IS one: the empty answer, logged, never a page that
    // loads forever).
    private static IObservable<(IReadOnlyList<PackageManifest> Packages, bool Answered)> ObserveAvailable(
        LayoutAreaHost host, IPackageSource? source, string sourceRef)
    {
        if (source is null)
            return Observable.Return((Packages: (IReadOnlyList<PackageManifest>)[], Answered: true));
        return source.ListPackages(sourceRef)
            .Select(packages => (Packages: packages, Answered: true))
            .Catch<(IReadOnlyList<PackageManifest> Packages, bool Answered), Exception>(ex =>
            {
                Logger(host)?.LogWarning(ex, "Catalog: failed to list packages @ {Ref}", sourceRef);
                return Observable.Return((Packages: (IReadOnlyList<PackageManifest>)[], Answered: true));
            })
            .StartWith((Packages: (IReadOnlyList<PackageManifest>)[], Answered: false));
    }

    // Selects the git-based package source for a repo path/subdir (delegates to the shared factory so
    // the node view and the registry endpoints build sources identically). Null when unconfigured.
    internal static IPackageSource? BuildSource(LayoutAreaHost host, string? sourceRepoPath, string? sourceSubdir) =>
        PackageSources.FromRepo(host.Hub, sourceRepoPath, sourceSubdir, Logger(host));

    /// <summary>
    /// The live installed-plugin inventory: every <c>Package</c> record in the install registry,
    /// deserialized to its <see cref="PackageManifest"/> and sorted by display name. This is the
    /// read-only "what is running on this instance" view the About tab shows every user — the
    /// catalog's ALL page joins the SAME records against a package source for install status.
    /// </summary>
    public static IObservable<IReadOnlyList<PackageManifest>> ObserveInstalledManifests(LayoutAreaHost host)
        => ObserveInstalled(host).Select(nodes => (IReadOnlyList<PackageManifest>)nodes
            .Select(n => n.ContentAs<PackageManifest>(host.Hub.JsonSerializerOptions))
            .Where(m => m is not null && !string.IsNullOrEmpty(m!.Id))
            .Select(m => m!)
            .OrderBy(m => m.Name ?? m.Id, StringComparer.OrdinalIgnoreCase)
            .ToList());

    // The install records a card page joins against: the whole registry for the ALL page (its
    // orphan section needs every record), and for a category page ONLY its members — one exact-path
    // read each, batched into one request, so rendering one category never loads the installed-file
    // baseline of every package on the instance.
    private static IObservable<IReadOnlyList<MeshNode>> ObserveInstalledFor(LayoutAreaHost host, CatalogPlan plan)
    {
        if (plan.Kind == CatalogPage.All)
            return ObserveInstalled(host);
        var queries = InstalledRecordQueries(plan.Packages.Select(p => p.Id));
        var mesh = host.Hub.ServiceProvider.GetService<IMeshService>();
        if (queries.Count == 0 || mesh is null)
            return Observable.Return<IReadOnlyList<MeshNode>>([]);
        return FoldInstalled(mesh.Query<MeshNode>(new MeshQueryRequest { Queries = queries }));
    }

    // Live map of installed packages (the Plugins registry children), as a list — content and all.
    private static IObservable<IReadOnlyList<MeshNode>> ObserveInstalled(LayoutAreaHost host)
    {
        var mesh = host.Hub.ServiceProvider.GetService<IMeshService>();
        if (mesh is null)
            return Observable.Return<IReadOnlyList<MeshNode>>([]);
        return FoldInstalled(mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(AllInstalledQuery)));
    }

    // The ids of every installed package, off the SHELL-only listing — what a category page's
    // Install click needs to skip already-installed dependencies from other categories, without the
    // records' content. The ALL page has every record in hand, so it reads nothing extra here.
    private static IObservable<ImmutableHashSet<string>> ObserveInstalledIds(LayoutAreaHost host, CatalogPlan plan)
    {
        var mesh = host.Hub.ServiceProvider.GetService<IMeshService>();
        if (plan.Kind == CatalogPage.All || mesh is null)
            return Observable.Return(ImmutableHashSet<string>.Empty);
        return FoldInstalled(mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(InstalledIdsQuery)))
            .Select(nodes => nodes
                .Select(n => n.Id)
                .Where(id => !string.IsNullOrEmpty(id))
                .ToImmutableHashSet(StringComparer.Ordinal));
    }

    // Folds a query's change stream into the current path-keyed set, seeded empty so the page never
    // waits on the registry's first frame.
    private static IObservable<IReadOnlyList<MeshNode>> FoldInstalled(IObservable<QueryResultChange<MeshNode>> changes) =>
        changes
            .Scan(ImmutableDictionary<string, MeshNode>.Empty, (map, change) =>
            {
                if (change.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset)
                    return change.Items.ToImmutableDictionary(n => n.Path);
                foreach (var item in change.Items)
                    map = change.ChangeType switch
                    {
                        QueryChangeType.Added or QueryChangeType.Updated => map.SetItem(item.Path, item),
                        QueryChangeType.Removed => map.Remove(item.Path),
                        _ => map
                    };
                return map;
            })
            .Select(m => (IReadOnlyList<MeshNode>)m.Values.ToList())
            .StartWith((IReadOnlyList<MeshNode>)[]);

    // The page frame every catalog page opens with: title, the authored intro, the source line.
    private static StackControl Frame(
        LayoutAreaHost host, IPackageSource? source, string? description, string? sourceLabel, int total)
    {
        var container = Controls.Stack
            .WithWidth("100%")
            .WithStyle("width: 100%; max-width: 900px; margin: 0 auto; padding: 16px;");

        container = container.WithView(
            Controls.H1(host.Localize("ui.pluginCatalog")).WithStyle("margin: 0 0 4px 0;"), "title");

        if (!string.IsNullOrWhiteSpace(description))
            container = container.WithView(
                Controls.Markdown(description!).WithStyle("margin-bottom: 8px;"), "description");

        var sourceLine = source is null
            ? host.Localize("ui.catalogNoSource")
            : host.Localize("ui.catalogSourceSummary",
                sourceLabel ?? host.Localize("ui.catalogRegistry"),
                host.LocalizePlural("plural.package", total));
        return container.WithView(Controls.Body(sourceLine)
            .WithStyle("color: var(--neutral-foreground-hint); margin-bottom: 16px; display: block;"), "source");
    }

    // The label a category key renders as: the source's own spelling, or the localized bucket name.
    private static string CategoryLabel(LayoutAreaHost host, string key) =>
        IsUncategorized(key) ? host.Localize("ui.catalogUncategorized") : key;

    // THE LANDING: one tile per category plus the all-packages entry — a way in, not a wall. Built
    // from the manifest listing alone.
    private static UiControl BuildLanding(
        LayoutAreaHost host, IPackageSource? source, string? description, string? sourceLabel, CatalogPlan plan)
    {
        var container = Frame(host, source, description, sourceLabel, plan.Total);
        if (plan.Total == 0)
            return container.WithView(Controls.Markdown(host.Localize("ui.mdNoPackages")), "empty");

        var grid = Controls.Stack
            .WithStyle("display: grid; grid-template-columns: repeat(auto-fill, minmax(210px, 1fr)); "
                       + "gap: 14px; margin: 12px 0; width: 100%;");
        var n = 0;
        foreach (var category in plan.Categories)
        {
            n++;
            grid = grid.WithView(
                Tile(host, CategoryLabel(host, category.Key), category.Count,
                    CategoryHref(host.Hub.Address, category.Key)),
                $"cat-{n}");
        }
        grid = grid.WithView(
            Tile(host, host.Localize("ui.catalogAllPackages"), plan.Total, AllHref(host.Hub.Address)), "all");
        return container.WithView(grid, "categories");
    }

    // One clickable tile: the name and a package count; the click is a plain in-app navigation, so
    // the browser's back button returns to the tiles.
    private static UiControl Tile(LayoutAreaHost host, string label, int count, string href) =>
        Controls.Stack
            .WithStyle("cursor: pointer; border: 1px solid var(--neutral-stroke-rest); border-radius: 12px; "
                       + "padding: 16px; min-height: 92px; background: var(--neutral-layer-1); "
                       + "display: flex; flex-direction: column; justify-content: space-between; gap: 6px;")
            .WithView(Controls.Body(label)
                .WithStyle("font-weight: 700; font-size: 1.05rem; display: block;"), "name")
            .WithView(Controls.Body(host.LocalizePlural("plural.package", count))
                .WithStyle("color: var(--neutral-foreground-hint); font-size: 0.85rem; display: block;"), "count")
            .WithClickAction(ctx =>
            {
                ctx.NavigateTo(href);
                return Task.CompletedTask;
            });

    // A CARD page: one category's cards, or every card plus the orphan section on the ALL page.
    private static UiControl BuildPackages(
        LayoutAreaHost host, IPackageSource? source, string sourceRef, string? description, string? sourceLabel,
        CatalogPlan plan, IReadOnlyList<MeshNode> installed, ImmutableHashSet<string> installedIds,
        bool viewerIsGlobalAdmin, ModuleActivationReport activation)
    {
        var installedById = installed
            .Select(n => n.ContentAs<PackageManifest>(host.Hub.JsonSerializerOptions))
            .Where(m => m is not null && !string.IsNullOrEmpty(m!.Id))
            .GroupBy(m => m!.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First()!, StringComparer.Ordinal);

        var container = Frame(host, source, description, sourceLabel, plan.Total);

        container = container.WithView(Controls.Button(host.Localize("ui.catalogBackToCategories"))
            .WithClickAction(ctx =>
            {
                ctx.NavigateTo(LandingHref(host.Hub.Address));
                return Task.CompletedTask;
            })
            .WithStyle("align-self: flex-start; margin: 0 0 8px 0;"), "back");

        container = container.WithView(Controls.H2(plan.Kind == CatalogPage.All
                ? host.Localize("ui.catalogAllPackages")
                : CategoryLabel(host, plan.Category!))
            .WithStyle("margin: 8px 0 4px 0;"), "heading");

        if (plan.Packages.Count == 0)
            container = container.WithView(Controls.Markdown(host.Localize("ui.mdNoPackages")), "empty");

        // The whole listing + what is already installed are what a click needs to resolve the
        // package's dependency closure (PackageDependencyGraph.InstallClosure) — the listing is in
        // hand; the installed set is the shell listing plus the records this page read.
        var knownInstalled = installedIds.Union(installedById.Keys);

        var n = 0;
        foreach (var pkg in plan.Packages)
        {
            n++;
            installedById.TryGetValue(pkg.Id, out var inst);
            container = container.WithView(
                BuildCard(host, source, sourceRef, pkg, inst, viewerIsGlobalAdmin, plan.Available, knownInstalled,
                    activation),
                $"pkg-{n}");
        }

        if (plan.Kind != CatalogPage.All)
            return container;

        var orphans = Orphaned(plan.Available, installed, host.Hub.JsonSerializerOptions);
        if (orphans.Count > 0)
        {
            container = container.WithView(Controls.H2(host.Localize("ui.orphanedInstallRecords"))
                .WithStyle("margin: 24px 0 4px 0;"));
            container = container.WithView(Controls.Markdown(host.Localize("ui.mdOrphanedInstallRecords"))
                .WithStyle("margin-bottom: 8px;"));
            var o = 0;
            foreach (var orphan in orphans)
            {
                o++;
                container = container.WithView(
                    BuildOrphanCard(host, orphan, viewerIsGlobalAdmin), $"orphan-{o}");
            }
        }

        return container;
    }

    /// <summary>
    /// The install records this source no longer offers — a record whose package left the registry
    /// (#840). These have no catalog card, so before this list existed nothing could remove them:
    /// the <c>Plugins/_Policy</c> caps delete for every user identity, and the only system-identity
    /// removal was the (card-driven) Uninstall.
    ///
    /// <para>Deliberately computed ONLY against a NON-EMPTY available list. An empty list means
    /// either "the registry offers nothing" or "listing it failed" (<see cref="ObserveAvailable"/>
    /// catches a failure to an empty list, and the stream starts empty) — and those are
    /// indistinguishable here. Offering to remove EVERY install record because a registry was
    /// briefly unreachable is exactly the kind of destructive guess this must never make.</para>
    /// </summary>
    internal static IReadOnlyList<PackageManifest> Orphaned(
        IReadOnlyList<PackageManifest> available, IReadOnlyList<MeshNode> installed,
        System.Text.Json.JsonSerializerOptions options)
    {
        if (available.Count == 0)
            return [];
        var availableIds = available.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        return installed
            .Select(n => n.ContentAs<PackageManifest>(options))
            .Where(m => m is not null && !string.IsNullOrEmpty(m!.Id) && !availableIds.Contains(m.Id))
            .Select(m => m!)
            .OrderBy(m => m.Name ?? m.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // An orphaned install record: what it says it is, and — for a global admin — the Remove action
    // that runs the installer's system-impersonated removal (the same identity that wrote it).
    private static UiControl BuildOrphanCard(
        LayoutAreaHost host, PackageManifest orphan, bool viewerIsGlobalAdmin)
    {
        var card = Controls.Stack
            .WithWidth("100%")
            .WithStyle("border: 1px dashed var(--neutral-stroke-rest); border-radius: 8px; " +
                       "padding: 14px 16px; margin-bottom: 12px;");

        card = card.WithView(Controls.Body(orphan.Name ?? orphan.Id)
            .WithStyle("font-weight: 600; font-size: 16px; display: block; margin-bottom: 4px;"));

        card = card.WithView(Controls.Body(
                $"{orphan.Id}  ·  v{orphan.Version}  ·  → {orphan.TargetPartition ?? orphan.Id}")
            .WithStyle("color: var(--neutral-foreground-hint); font-size: 12px; display: block; margin-bottom: 10px;"));

        if (!viewerIsGlobalAdmin)
            return card.WithView(Controls.Body(host.Localize("ui.requiresGlobalAdmin"))
                .WithStyle("color: var(--neutral-foreground-hint); font-size: 12px; display: block;"));

        return card.WithView(Controls.Button(host.Localize("ui.removeInstallRecord"))
            .WithClickAction(ctx =>
            {
                RemoveInstallRecord(host, orphan.Id);
                return Task.CompletedTask;
            }));
    }

    /// <summary>
    /// Removes an orphaned install record through the installer's sanctioned system-impersonated
    /// primitive. The AUTHORIZATION is the global-admin gate on the surface that offered the action
    /// (<see cref="BuildOrphanCard"/>) — the same "the click authorizes, the SYSTEM executes"
    /// division the install path uses; the removal itself must run as System because the
    /// <c>Plugins</c> partition policy denies delete to every user identity by design.
    /// </summary>
    internal static void RemoveInstallRecord(LayoutAreaHost host, string packageId)
    {
        var logger = Logger(host);
        PackageInstaller.RemoveInstalledRecord(host.Hub, packageId, logger)
            .Subscribe(
                removed => logger?.LogInformation(
                    "Orphaned install record {Id}: {Result}.", packageId, removed ? "removed" : "not found"),
                ex => logger?.LogWarning(ex, "Removing orphaned install record {Id} failed.", packageId));
    }

    private static UiControl BuildCard(
        LayoutAreaHost host, IPackageSource? source, string sourceRef, PackageManifest pkg,
        PackageManifest? installed, bool viewerIsGlobalAdmin,
        IReadOnlyList<PackageManifest> catalog, IReadOnlySet<string> installedIds,
        ModuleActivationReport activation)
    {
        var card = Controls.Stack
            .WithWidth("100%")
            .WithStyle("border: 1px solid var(--neutral-stroke-rest); border-radius: 8px; " +
                       "padding: 14px 16px; margin-bottom: 12px;");

        card = card.WithView(Controls.Body(pkg.Name ?? pkg.Id)
            .WithStyle("font-weight: 600; font-size: 16px; display: block; margin-bottom: 4px;"));

        if (!string.IsNullOrWhiteSpace(pkg.Description))
            card = card.WithView(Controls.Body(pkg.Description!).WithStyle("display: block; margin-bottom: 6px;"));

        card = card.WithView(Controls.Body($"v{pkg.Version}  ·  {pkg.Kind}  ·  → {pkg.TargetPartition}")
            .WithStyle("color: var(--neutral-foreground-hint); font-size: 12px; display: block; margin-bottom: 10px;"));

        // ModuleVersion (the module's OWN content hash from manifest.lock) beats the whole-repo
        // commit sha: an unrelated commit no longer flips every card to "Update". The commit-sha
        // compare stays the fallback for manifest-less packages.
        var upToDate = installed is not null
            && (!string.IsNullOrEmpty(pkg.ModuleVersion) && !string.IsNullOrEmpty(installed.ModuleVersion)
                ? string.Equals(installed.ModuleVersion, pkg.ModuleVersion, StringComparison.Ordinal)
                : string.Equals(installed.Version, pkg.Version, StringComparison.Ordinal));

        if (upToDate)
        {
            card = card.WithView(Controls.Body(host.Localize("ui.catalogInstalledVersion", installed!.Version))
                .WithStyle("color: var(--success-foreground, #107c10); font-weight: 600;"));
        }
        else if (pkg.IsCommercial() && !viewerIsGlobalAdmin)
        {
            // A commercial package needs Global Admin to install or sync (#830). The real
            // enforcement is on the ACTION (PackageEntitlement, inside the installer); this is only
            // so a viewer is not offered a button whose click would be refused.
            card = card.WithView(Controls.Body(host.Localize("ui.requiresGlobalAdmin"))
                .WithStyle("color: var(--neutral-foreground-hint); font-size: 12px; display: block;"));
        }
        else if (source is not null)
        {
            var label = installed is null
                ? host.Localize("ui.catalogInstall")
                : host.Localize("ui.catalogUpdateTo", pkg.Version);
            card = card.WithView(Controls.Button(label)
                .WithAppearance(Appearance.Accent)
                .WithClickAction(ctx =>
                {
                    InstallPackage(host, source, sourceRef, pkg, catalog, installedIds);
                    return Task.CompletedTask;
                }));
        }

        // 🚨 The LAST STEP of the install, said out loud (#1979). Loading a module is
        // restart-as-activation by design, so for a package that declares one the install is not
        // finished when the content lands — and until this line existed nothing anywhere said so:
        // the card read "✓ Installed", the feature was absent, and the person who installed it had
        // no way to learn that a restart was the missing half. Rendered UNDER the status line
        // rather than instead of it, because both facts are true: the package IS installed, and its
        // module is not running yet.
        //
        // Derived from the report the operator health check reads, so the two surfaces can never
        // tell different stories — and matched on the install record's PATH, which is exactly what
        // the landing recorded on the activation entry (InstallOrUpdateCore passes
        // "{InstalledPartition}/{pkg.Id}" to AdoptModule). A blank/undetermined answer renders
        // nothing: see ModuleActivationReport.IsPendingForPackage for why silence is the honest
        // fallback here.
        if (activation.IsPendingForPackage($"{PackageInstaller.InstalledPartition}/{pkg.Id}"))
            card = card.WithView(Controls.Body($"🔄 {host.Localize("ui.restartRequiredToActivate")}")
                .WithStyle("color: var(--warning-foreground, #9d5d00); font-size: 12px; "
                           + "display: block; margin-top: 6px;"));

        return card;
    }

    /// <summary>
    /// Fire the install; the Plugins-registry stream re-emits on completion → the card flips.
    ///
    /// <para>Installs the package's DEPENDENCY CLOSURE, not just the package: every requirement
    /// (<see cref="PackageManifest.Requires"/>) the instance does not yet have is installed first,
    /// in dependency order, on the one Concat so each finishes before the next begins. Without it a
    /// click on a dependent simply fails — the installer refuses an instance whose NodeType is not
    /// present ("NodeType(s) not registered: Training/Tour"), naming a path that appears in neither
    /// the package the user clicked nor any error they can act on. The unattended boot pass
    /// (<see cref="InstanceAutoRegistrationService"/>) has derived this order for a while; the
    /// click did not, which is the half #636 closes.</para>
    /// </summary>
    /// <param name="catalog">Every package the source offers — the dependency resolution universe.
    /// Omitted (a single-package caller) means only <paramref name="pkg"/> installs, exactly as
    /// before.</param>
    /// <param name="installedIds">Package ids already in the install registry; those dependencies
    /// are skipped rather than re-installed.</param>
    /// <exception cref="InvalidOperationException">The package's declared dependencies form a
    /// cycle. Thrown, not swallowed: it propagates out of the click action to
    /// <c>LayoutAreaHost.OnClick</c> → <c>FailRequest</c>, so the action fails visibly instead of
    /// leaving the clicker with a button that silently did nothing.</exception>
    internal static void InstallPackage(
        LayoutAreaHost host, IPackageSource source, string sourceRef, PackageManifest pkg,
        IReadOnlyList<PackageManifest>? catalog = null, IReadOnlySet<string>? installedIds = null)
    {
        var logger = Logger(host);

        // 🚨 The install runs under SYSTEM for its WHOLE lifetime — an install is PROVISIONING,
        // not a user data write. Post core #804 every partition the installer creates lands under
        // the System identity with no user grants, so the CLICKING user legitimately holds NOTHING
        // on it mid-install — any step that authorises against the ambient identity then fails
        // closed. The previous code re-established the clicking USER's context here instead, and
        // #817's batch topology made exactly such a step deterministic: the self-typed root's
        // reconciliation read (PackageInstaller.RootRetypeReconciled → the per-user gate in
        // MeshNodeStreamCache) ran as the user and every Store install died with
        // "User '…' lacks Read permission on 'Store'" (education CI, 2026-08-05, first image
        // carrying #817). System is also what the OTHER install triggers already do — the
        // PluginUpdateWatcher wraps this very InstallOrUpdate in ImpersonateAsSystem, and the
        // Store plugin's SystemInstall/Provisioning sources do the same. Authorisation for the
        // TRIGGER stays where it belongs: on the catalog surface the click came from.
        // REQUIRED, never optional: a missing AccessService would silently run the install under
        // the ambient (user) identity — the exact regression this fix removes. Same treatment the
        // PluginUpdateWatcher already gives it.
        var accessService = host.Hub.ServiceProvider.GetRequiredService<AccessService>();

        // WHO authorized the install — captured HERE, while the ambient identity is still the
        // clicking user's, because the install below runs entirely as System. A commercial package
        // requires this principal to be a global admin (#830); a free one ignores it. The check
        // itself lives in the installer, so the machine paths cannot bypass it.
        var authorizingUserId = ResolveViewerId(host);

        IReadOnlyList<PackageManifest> closure;
        try
        {
            closure = PackageDependencyGraph.InstallClosure(
                pkg, catalog ?? [pkg], installedIds ?? ImmutableHashSet<string>.Empty, logger);
        }
        catch (InvalidOperationException ex)
        {
            // A declared cycle: there is no order that works, so installing anything would fail
            // later with a NodeType path naming neither package. Refuse with the named loop —
            // the boot pass deliberately keeps the tolerant behaviour instead (it must not strand
            // a whole instance over one malformed package).
            // 🚨 Logged AND RETHROWN, never swallowed. Returning here would leave the clicker with
            // a button that did nothing and no feedback at all; the throw propagates out of the
            // click action into LayoutAreaHost.OnClick, which routes it through FailRequest so the
            // action visibly fails. The message already names the loop ("A → B → A").
            logger?.LogWarning(ex, "Install of {Id} refused: {Reason}", pkg.Id, ex.Message);
            throw;
        }

        if (closure.Count > 1)
            logger?.LogInformation(
                "Installing {Id} with {Count} dependency package(s) first — {Closure}",
                pkg.Id, closure.Count - 1, string.Join(", ", closure.Select(p => p.Id)));

        // Sequential (Concat): a dependency's install must COMPLETE before the dependent's begins,
        // which is what makes its NodeType nodes present for the dependent's type validation.
        var install = closure
            .Select(p => InstallOrUpdate(host.Hub, source, sourceRef, p, logger, authorizingUserId)
                .Do(result => logger?.LogInformation(
                    "Installed {Id}: {Written} written, {Unchanged} unchanged.",
                    p.Id, result.Written, result.Unchanged))
                // 🚨 Name the package that ACTUALLY failed. On a closure install the failing step
                // is frequently a DEPENDENCY, and reporting only the clicked package misleads
                // exactly when someone is troubleshooting ("Install of Chess failed" when it was
                // Training that broke). Logged HERE, where the step's own id is in scope, then
                // rethrown so the Concat aborts — a dependent must never install after its
                // dependency failed.
                .Catch((Exception ex) =>
                {
                    logger?.LogWarning(ex,
                        "Install of {Id} failed (while installing {Target} and its dependencies).",
                        p.Id, pkg.Id);
                    return Observable.Throw<InstallResult>(ex);
                }))
            .ToObservable()
            .Concat();

        // 🚨 RunAsSystem, never Observable.Using (#1790). A click action subscribes on the Blazor
        // circuit's own thread; Observable.Using would leave `system-security` latched there for
        // everything the circuit does next, and hand the install's terminating thread the clicking
        // user's identity. RunAsSystem opens the scope across the cold install's Subscribe — where
        // every write eager-captures its identity — and closes it on the way out of it.
        accessService.RunAsSystem(() => install)
            .Subscribe(
                _ => { },
                // The failing package is already named above; this records that the CLICK did not
                // complete, which is the different fact a reader of this line needs.
                ex => logger?.LogWarning(ex,
                    "Installing {Id} and its dependencies did not complete.", pkg.Id));
    }

    /// <summary>
    /// The install/update orchestrator. For a manifest-carrying node-repo package it skips or
    /// narrows the work by the module manifest: an installed record with the SAME
    /// <see cref="PackageManifest.ModuleVersion"/> means nothing to sync (no fetch, no record
    /// rewrite); a differing one fetches only <c>manifest.lock</c>, diffs it against the record's
    /// installed-files baseline and updates just the changed nodes (pruning removed ones). Every
    /// other case — no manifest, no baseline, a shared-Source change (whose blast radius is every
    /// type in the package), or ANY error on the incremental path — falls back to the full install
    /// (<see cref="PackageInstaller.Install"/>), which prunes nodes the repo retired against the
    /// SAME previous-record baseline whenever one exists (Systemorph/MeshWeaver#2473) — a node this
    /// package shipped before but not any more never merely survives because the incremental path
    /// declined to touch it.
    /// </summary>
    internal static IObservable<InstallResult> InstallOrUpdate(
        IMessageHub hub, IPackageSource source, string sourceRef, PackageManifest pkg, ILogger? logger,
        string? authorizingUserId = null)
    {
        // The entitlement gate runs FIRST, before a single file travels: a commercial package needs
        // a global admin (#830), and fetching a package that may not be installed is work nobody
        // asked for. The installer carries the same gate — that one is the enforcement (no caller
        // can bypass it), this one is where the refusal is cheapest.
        return PackageEntitlement.Authorize(hub, pkg, authorizingUserId, logger)
            // …then the PARAMETER gate, on the same funnel and for the same reason. A package that
            // declares a required connection string / endpoint the environment does not supply is
            // refused here, naming the exact env var to provision — never installed half-configured
            // and never silently skipped. Every lane goes through this method (boot default install,
            // the Store's Provision click, the auto-update reconciler), so this is the ONE place it
            // needs to sit.
            .SelectMany(_ => PackageParameters.Require(hub, pkg, logger))
            .SelectMany(_ => InstallOrUpdateCore(hub, source, sourceRef, pkg, logger, authorizingUserId));
    }

    private static IObservable<InstallResult> InstallOrUpdateCore(
        IMessageHub hub, IPackageSource source, string sourceRef, PackageManifest pkg, ILogger? logger,
        string? authorizingUserId)
    {
        // The module branch of the install funnel (#1664 Slice C): a package that DECLARES a
        // compiled module routes its binary payload — AFTER the content lands — to bundle-fetch →
        // MVID gate → ModuleLandingService (restart-as-activation), never through the node parse.
        // Riding here, on the ONE orchestrator, means every install path gets it identically: the
        // catalog card's click, the content auto-update apply, and the boot default install. Only
        // a registry source can serve a bundle (a git source has repo files and no bake behind
        // it — the registry instance itself runs its modules from its own image/modules tree), and
        // AdoptModule absorbs every failure into a logged zero, so this can never fail an install.
        IObservable<InstallResult> WithModule(IObservable<InstallResult> install) =>
            string.IsNullOrWhiteSpace(pkg.Module)
            || (source as RegistryPackageSource)?.Bundles is not { } bundles
                ? install
                : install.SelectMany(result => bundles
                    .AdoptModule(pkg.Id, pkg.Module!,
                        $"{PackageInstaller.InstalledPartition}/{pkg.Id}")
                    .Select(_ => result));

        IObservable<InstallResult> Full() =>
            WithModule(source.FetchPackageFiles(pkg, sourceRef)
                .SelectMany(files => PackageInstaller.Install(
                    hub, pkg, files, sourceRef, logger,
                    authorizingUserId: authorizingUserId)));

        var persistence = hub.ServiceProvider.GetService<IStorageAdapter>();
        if (pkg.Kind != PackageKind.NodeRepo || string.IsNullOrEmpty(pkg.ModuleVersion) || persistence is null)
            return Full();

        // The authoritative install record (the same read UpsertIfChanged uses) — the diff baseline.
        return persistence.Read($"{PackageInstaller.InstalledPartition}/{pkg.Id}", hub.JsonSerializerOptions)
            .Take(1)
            .Select(n => n?.ContentAs<PackageManifest>(hub.JsonSerializerOptions))
            .Catch<PackageManifest?, Exception>(_ => Observable.Return<PackageManifest?>(null))
            .SelectMany(record =>
            {
                if (record is not null
                    && string.Equals(record.ModuleVersion, pkg.ModuleVersion, StringComparison.Ordinal))
                {
                    logger?.LogInformation(
                        "Package {Id} content is up to date (module {ModuleVersion}); nothing to sync.",
                        pkg.Id, pkg.ModuleVersion);
                    // 🚨 #2417 — WithModule, and the missing wrapper here is half of why a package
                    // could record as installed with no binary anywhere. This early return is a
                    // CONTENT verdict: the manifest hash the record was stamped from equals the
                    // one the source serves, so no node needs to travel. It says nothing whatever
                    // about the module — and by returning unwrapped (the other two exits below are
                    // both WithModule'd) it made the content answer stand in for the module
                    // answer. Once a moduleVersion was stamped, no install and no reconcile would
                    // ever ask about the binary again, on any deployment.
                    //
                    // The module lane costs nothing when there is nothing to ask: WithModule is
                    // the identity for a package declaring no module or a source that serves no
                    // bundles, and AdoptModule absorbs every failure into a logged zero — its
                    // presence-aware ModuleUpdateDecision answers SkipUpToDate for the normal
                    // case, in which nothing travels either.
                    return WithModule(Observable.Return(new InstallResult(0, 0)));
                }
                if (record?.InstalledFiles is not { Count: > 0 })
                    return Full();
                return WithModule(IncrementalUpdate(hub, source, sourceRef, pkg, record, logger, authorizingUserId))
                    .Catch<InstallResult, Exception>(ex =>
                    {
                        // A REFUSAL is not a failure to fall back from — the full install would be
                        // refused identically, and retrying it would bury the reason under a
                        // second, misleading log line.
                        if (ex is PackageAuthorizationException)
                            return Observable.Throw<InstallResult>(ex);
                        logger?.LogWarning(ex,
                            "Incremental update of {Id} failed; falling back to full install.", pkg.Id);
                        return Full();
                    });
            });
    }

    // The manifest-diff fast path: fetch only manifest.lock, diff, fetch only the changed files.
    private static IObservable<InstallResult> IncrementalUpdate(
        IMessageHub hub, IPackageSource source, string sourceRef, PackageManifest pkg,
        PackageManifest record, ILogger? logger, string? authorizingUserId)
    {
        var manifestPath = $"{pkg.Id}/{ModuleManifest.FileName}";
        return source.FetchPackageFiles(pkg, sourceRef, [manifestPath])
            .SelectMany(files =>
            {
                var newManifest = files
                    .Where(f => ModuleManifest.IsManifestPath(f.RelativePath))
                    .Select(f => ModuleManifest.TryParse(f.Content, logger))
                    .FirstOrDefault(m => m is not null);
                if (newManifest is null)
                    throw new InvalidOperationException(
                        $"Package '{pkg.Id}' ships no parseable {ModuleManifest.FileName}.");

                var delta = newManifest.DiffFrom(record.InstalledFiles);

                // A change to the package's SHARED Source/Test (partition-level compile inputs)
                // affects every type in the package — the full install's release-all handles that;
                // the delta's owner-derivation would miss siblings.
                var sharedPrefixes = new[] { $"{pkg.Id}/Source/", $"{pkg.Id}/Test/" };
                if (delta.AddedOrChangedFiles.Concat(delta.RemovedFiles)
                    .Any(p => sharedPrefixes.Any(s => p.StartsWith(s, StringComparison.Ordinal))))
                    throw new InvalidOperationException(
                        $"Package '{pkg.Id}' changed shared Source/Test files; full install required.");

                var changedNodePaths = delta.AddedOrChangedFiles
                    .Select(PackageInstaller.NodePathForFile)
                    .Where(p => p is not null).Select(p => p!)
                    .ToHashSet(StringComparer.Ordinal);
                // Removed FILES prune their nodes — unless the node is still fed by a changed file
                // (the `X.json` → `X/index.json` layout move maps both to node X).
                var removedNodePaths = delta.RemovedFiles
                    .Select(PackageInstaller.NodePathForFile)
                    .Where(p => p is not null && !changedNodePaths.Contains(p))
                    .Select(p => p!)
                    .ToHashSet(StringComparer.Ordinal);

                logger?.LogInformation(
                    "Updating {Id} incrementally: {Changed} changed file(s), {Removed} removed → module {ModuleVersion}.",
                    pkg.Id, delta.AddedOrChangedFiles.Count, delta.RemovedFiles.Count, newManifest.ModuleVersion);

                return (delta.AddedOrChangedFiles.Count == 0
                        ? Observable.Return((IReadOnlyList<PackageFile>)[])
                        : source.FetchPackageFiles(pkg, sourceRef, delta.AddedOrChangedFiles))
                    .SelectMany(changedFiles => PackageInstaller.InstallNodeRepoDelta(
                        hub, pkg, newManifest, changedFiles, removedNodePaths, sourceRef, logger,
                        authorizingUserId));
            });
    }

    private static ILogger? Logger(LayoutAreaHost host) =>
        host.Hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.PluginCatalog.Catalog");
}
