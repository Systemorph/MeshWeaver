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
using MeshWeaver.Hosting.Persistence.Parsers;
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
public static partial class CatalogLayoutAreas
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
                // #4500: this page replaced the framework Overview and silently dropped the
                // provenance line with it. A catalog node is content — it is declared, published
                // and re-pointed by people — so "who set this source up, and when" is a question
                // its page is genuinely asked. WithNodePage composes the line above the catalog.
                .WithNodePage(MeshNodeLayoutAreas.OverviewArea, Overview)
                .WithView(CatalogArea, Catalog));
            // Create / Delete are no longer re-registered here: their views ride the
            // MeshWeaver.Graph.Views module, which registers them on every per-node hub, so this
            // hub gets them without naming an implementation the platform no longer carries.

    /// <summary>The default Overview is the catalog.</summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext ctx) => Catalog(host, ctx);

    /// <summary>
    /// Renders the catalog for a <c>PluginCatalog</c> node: builds the source from the node's
    /// <see cref="PluginCatalogContent"/> and binds it into <see cref="CatalogTemplate"/> through <see cref="CatalogFeed"/>.
    /// </summary>
    /// <param name="host">The layout area host rendering the area.</param>
    /// <param name="_">The rendering context for the area.</param>
    /// <returns>An observable stream of the catalog view.</returns>
    [Browsable(false)]
    public static IObservable<UiControl?> Catalog(LayoutAreaHost host, RenderingContext _)
        // A TEMPLATE (Doc/GUI/DataBinding → "Templates first, data later"): the page is declared at
        // once and CatalogFeed — the node's source, its listing, the install registry — binds into it.
        => Observable.Return<UiControl?>(CatalogTemplate(host, CatalogFeed(host)));

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
        // 🚨 #4097 — the registry's plan-tier refusals ride the listing as refused rows: a
        // pre-installed package the registry declares in this instance's default set but its plan
        // does not cover gets a CARD saying so, not an absent entry. Only a registry source can
        // answer with refusals; every other source is its plain listing.
        var listing = source is RegistryPackageSource registry
            ? registry.ListCatalog(sourceRef).Select(l => (IReadOnlyList<PackageManifest>)l.Packages
                .Concat(l.Refused
                    .Where(r => !l.Packages.Any(p => string.Equals(p.Id, r.PackageId, StringComparison.Ordinal)))
                    .Select(r => PackageManifest.FromRefusal(r, null)))
                .ToList())
            : source.ListPackages(sourceRef);
        return listing
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
    internal static IPackageSource? BuildSource(
        LayoutAreaHost host, string? sourceRepoPath, string? sourceSubdir, string? format = null) =>
        PackageSources.FromRepo(
            host.Hub, sourceRepoPath, sourceSubdir, Logger(host),
            PackageSources.IsNodeRepoFormatOrDetected(format, sourceRepoPath, sourceSubdir));

    /// <summary>
    /// The live installed-plugin inventory: every <c>Package</c> record in the install registry,
    /// deserialized to its <see cref="PackageManifest"/> and sorted by display name. This is the
    /// read-only "what is running on this instance" view the About tab shows every user — the
    /// catalog's ALL page joins the SAME records against a package source for install status.
    ///
    /// <para>🚨 Two properties a consumer can rely on. A live instance's Overview said "No plugins are
    /// installed on this instance" over dozens of installs; the first property is the reproduced
    /// cause (<c>AdminAppFirstFrameTest</c>), the second hardens the read against losing its
    /// viewer:</para>
    /// <list type="bullet">
    /// <item><description><b>It emits the registry's ANSWER, never a placeholder.</b> The first
    /// emission is the query's Initial — so an empty list means the registry IS empty, and a view
    /// may say so. (The catalog pages seed their own frame; this inventory does not.)</description></item>
    /// <item><description><b>It reads as the VIEWER the page renders for</b>, stamped explicitly
    /// (<see cref="MeshQueryRequest.ForViewer"/>) from the host's viewer rather than resolved from
    /// the ambient context when the query subscribes — which, for a view rendered on a live
    /// emission on a distributed mesh, is nobody: the anonymous view, and on an instance closed to
    /// logged-out callers that is an empty registry.</description></item>
    /// </list>
    /// </summary>
    public static IObservable<IReadOnlyList<PackageManifest>> ObserveInstalledManifests(LayoutAreaHost host)
    {
        var mesh = host.Hub.ServiceProvider.GetService<IMeshService>();
        if (mesh is null)
            return Observable.Return<IReadOnlyList<PackageManifest>>([]);
        var request = MeshQueryRequest.FromQuery(AllInstalledQuery);
        // The subscriber the page was opened for first; the ambient context only when the host
        // carries none. A logged-out (virtual) visitor or a hub credential is never stamped as a
        // signed-in viewer — those reads keep the framework's own resolution.
        var access = host.Hub.ServiceProvider.GetService<AccessService>();
        if ((host.ViewerContext ?? access?.Context ?? access?.CircuitContext)
            is { ObjectId: { Length: > 0 } viewerId, IsVirtual: false, IsHub: false })
            request = request.ForViewer(viewerId);
        return FoldInstalledAnswers(mesh.Query<MeshNode>(request))
            .Select(nodes => (IReadOnlyList<PackageManifest>)nodes
                .Select(n => n.ContentAs<PackageManifest>(host.Hub.JsonSerializerOptions))
                .Where(m => m is not null && !string.IsNullOrEmpty(m!.Id))
                .Select(m => m!)
                .OrderBy(m => m.Name ?? m.Id, StringComparer.OrdinalIgnoreCase)
                .ToList());
    }

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
        FoldInstalledAnswers(changes).StartWith((IReadOnlyList<MeshNode>)[]);

    // The same fold WITHOUT the seed: every emission is the registry's answer (its Initial, then
    // each change), so an empty list means empty — never "not answered yet".
    private static IObservable<IReadOnlyList<MeshNode>> FoldInstalledAnswers(IObservable<QueryResultChange<MeshNode>> changes) =>
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
            .Select(m => (IReadOnlyList<MeshNode>)m.Values.ToList());

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

    /// <summary>
    /// Removes an orphaned install record through the installer's sanctioned system-impersonated
    /// primitive. The AUTHORIZATION is the global-admin gate on the surface that offered the action
    /// (<see cref="RemoveClicked"/>) — the same "the click authorizes, the SYSTEM executes"
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
            // #4097 — a plan-tier refusal is a card, never a dependency universe member: a
            // closure that pulled one in would fail at /files with the 404 the refusal replaces.
            closure = PackageDependencyGraph.InstallClosure(
                pkg, catalog?.Where(p => !p.IsRefused).ToList() ?? [pkg],
                installedIds ?? ImmutableHashSet<string>.Empty, logger);
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
            // …then the PLATFORM floor (policy package-min-mesh-version): an update whose candidate
            // declares a minMeshVersion above the running platform is HELD before a file travels —
            // the installed version keeps running and the record says why. Every lane funnels
            // here (the boot install, the Store's click, the auto-update apply).
            .SelectMany(_ => HoldIfPlatformBelowFloor(hub, pkg, logger,
                () => InstallOrUpdateCore(hub, source, sourceRef, pkg, logger, authorizingUserId)));
    }

    /// <summary>
    /// 🚨 The UPDATE half of policy <c>package-min-mesh-version</c> on the one install orchestrator:
    /// when <paramref name="pkg"/>'s declared floor is held on this platform
    /// (<see cref="PackagePlatformFloorGate.Evaluate"/>) and an install record with a DIFFERENT
    /// content hash exists, nothing is fetched or written — the installed version keeps running
    /// (rule R1), the record carries <see cref="PackageManifest.HeldUpdate"/>, and the result is an
    /// empty <see cref="InstallResult"/>. Everything else proceeds to <paramref name="proceed"/>:
    /// a satisfied or advisory floor, and a record at the SAME hash (the skip / heal path —
    /// re-landing what is already here replaces nothing). With no record at all the install is a
    /// FRESH one and is refused before anything is fetched
    /// (<see cref="PackagePlatformFloorGate.RequireForFreshInstall"/>).
    /// </summary>
    private static IObservable<InstallResult> HoldIfPlatformBelowFloor(
        IMessageHub hub, PackageManifest pkg, ILogger? logger, Func<IObservable<InstallResult>> proceed)
    {
        var verdict = PackagePlatformFloorGate.Evaluate(hub, pkg);
        if (!verdict.IsHeld)
            return proceed();

        var persistence = hub.ServiceProvider.GetService<IStorageAdapter>();
        if (persistence is null)
            return proceed();

        return persistence.Read($"{PackageInstaller.InstalledPartition}/{pkg.Id}", hub.JsonSerializerOptions)
            .Take(1)
            .DefaultIfEmpty()
            .Select(n => n?.ContentAs<PackageManifest>(hub.JsonSerializerOptions))
            .Catch<PackageManifest?, Exception>(_ => Observable.Return<PackageManifest?>(null))
            .SelectMany(record =>
                record is null
                    // A FRESH install: refused here, before a single file is fetched — the
                    // installer's own gate says the same thing to callers that reach it directly.
                    ? PackagePlatformFloorGate.RequireForFreshInstall(hub, pkg, logger).SelectMany(_ => proceed())
                    : !string.IsNullOrEmpty(pkg.ModuleVersion)
                      && string.Equals(record.ModuleVersion, pkg.ModuleVersion, StringComparison.Ordinal)
                        ? proceed()
                        : PackagePlatformFloorGate.RecordHold(hub, pkg, record, verdict, logger)
                            .Select(_ => new InstallResult(0, 0)));
    }

    /// <summary>
    /// Closes an install's landing wave by proposing the module set the activation record now
    /// describes (#3395) — the same step <c>RegistryUpdateReconciler</c> takes at the end of its
    /// auto-update wave, so both lanes move the mesh's set the one way. A deployment with no
    /// landing service (a consumer with no module store) proposes nothing, silently.
    /// </summary>
    private static IObservable<ModuleSet?> ProposeMeshModuleSet(IMessageHub hub, ILogger? logger)
    {
        var landing = hub.ServiceProvider.GetService<ModuleLandingService>();
        if (landing is null)
            return Observable.Return<ModuleSet?>(null);
        return landing.ProposeModuleSet()
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex,
                    "The module landed but its module set could not be proposed — the mesh stays "
                    + "on its current set and the next landing wave proposes again. Cause: {Cause}",
                    ex.Message);
                return Observable.Return<ModuleSet?>(null);
            });
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
                    // 🚨 #3395 — a one-package install IS a landing wave, and a wave that does not
                    // propose its module set never activates: boot loads the mesh's set, not the
                    // activation record it was derived from. Never fails the install: an
                    // unproposable set leaves the mesh on the one every replica already runs, and
                    // the next wave proposes again.
                    .SelectMany(_ => ProposeMeshModuleSet(hub, logger))
                    .Select(_ => result));

        IObservable<InstallResult> Full() =>
            Verified(WithModule(source.FetchPackageFiles(pkg, sourceRef)
                .SelectMany(files => PackageInstaller.Install(
                    hub, pkg, files, sourceRef, logger,
                    authorizingUserId: authorizingUserId))));

        // 🚨 #2387 — EVERY WRITING EXIT IS READ BACK. The verdict that decides the severity is the
        // one taken AFTER the write; the pre-check below only decides whether to skip. Attached
        // here rather than at each call site so no lane can acquire an unverified exit later: the
        // three writing paths (full install, incremental update, and the heal the pre-check
        // triggers — which calls Full) all funnel through this. The three SKIPPING exits do not,
        // and must not: they just observed, and observing twice would cost a second batched read
        // per package on every boot to re-learn what it already said.
        IObservable<InstallResult> Verified(IObservable<InstallResult> install) =>
            install.SelectMany(result => VerifyLanded(hub, pkg, logger).Select(_ => result));

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
                    // 🚨 #3485 — THE HASH COMPARE ABOVE IS A STATEMENT ABOUT THE SOURCE, NOT ABOUT
                    // THE MESH. `record.ModuleVersion` is a hash of the FILES the source serves,
                    // stamped by the installer onto a record the installer itself wrote. Nothing in
                    // it observes what actually landed — so a node lost AFTER the install (a delete,
                    // an interrupted sync, a partial write) leaves the two hashes equal and this
                    // early return served a partial install as healthy. That is not hypothetical:
                    // `memex.systemorph.com` lost `Feedback/Feedback/Source/FeedbackContent` on
                    // 2026-08-26, a REINSTALL on 2026-09-03 returned InstallResult(0, 0) without
                    // fetching a file, and eleven days later the gap was the proximate cause of the
                    // #3472 outage. The remedy an operator reaches for first had already been shown
                    // not to work, and no instrument anywhere said why.
                    //
                    // So the skip now needs a POSITIVE OBSERVATION that every node the record
                    // declares is actually in the mesh. The three non-Complete verdicts are kept
                    // apart on purpose (AGENTS.md — a check that answers a boolean about something
                    // it had to READ must not spell a failed read like a real negative):
                    //   • Incomplete   → DO NOT SKIP. The full install heals it (DecideAndWrite
                    //                    writes whenever `current is null`) and the shortfall is
                    //                    named at Error. This is the #3485 fix.
                    //   • Undeclared   → the record carries no file map, so there is nothing to
                    //   • NotObserved  → compare, or the mesh could not be read. Today's behaviour
                    //                    is preserved (skip — a full install of every unverifiable
                    //                    package on every boot would be a new cost nobody asked
                    //                    for) but it is NOT reported as verified: it says at
                    //                    Warning that completeness was not checked, and why.
                    return InstallCompleteness
                        .Observe(persistence, hub.JsonSerializerOptions, pkg.Id,
                            PackageInstaller.TargetPartitionOf(pkg.Id, record), record,
                            // The INSTALL's own parser registry — the declared population must be
                            // the files this install would write, never a superset (#3659).
                            new FileFormatParserRegistry(
                                hub.JsonSerializerOptions,
                                hub.ServiceProvider.GetServices<IFileFormatParser>()))
                        .SelectMany(verdict => SkipOrHeal(verdict, pkg, logger, Full, WithModule));
                }

                if (record?.InstalledFiles is not { Count: > 0 })
                    return Full();
                var baseline = record;

                // 🚨 #4355 — THE DELTA'S BASELINE IS A CLAIM ABOUT THE MESH, AND ONLY ITS OWNER MAY
                // MAKE IT. `record.InstalledFiles` describes what THIS installer last wrote. On a
                // partition a sync source keeps current, a second writer has rewritten the content
                // since — the seal reconciler re-imports the whole partition at the sealed commit and
                // does not touch this record — so the diff declares "unchanged" for files the mesh no
                // longer holds at that content and never fetches them. Measured on
                // memex.systemorph.com 2026-09-14: `Store/Core/Source/StoreTexts.cs` was identical
                // between the record's 1.10.14 and the served 1.11.1, was skipped, and stayed at the
                // git tree's 1.10.3 text while five sibling types landed at 1.11.1 —
                // `CS1061 'StoreTexts' does not contain a definition for 'ExploreCta'`.
                //
                // So where the installer does not own the content, the incremental path is not
                // available: a FULL install writes every file the package ships and stamps a record
                // that is true of the mesh again. `Undetermined` takes the same arm — a baseline that
                // could not be shown trustworthy is not a trustworthy baseline — and the extra cost
                // is one full package fetch, paid only when an update is actually landing (the
                // hash-equal skip above is unaffected) and only on a synced partition.
                //
                // This is the OTHER half of the invariant from the gate in PackageUpdateReconciler:
                // that one stops the unattended lane from BECOMING the second writer; this one holds
                // for every lane that still writes here by design — the seal-pinned boot install
                // (#4259) and a human's Update click — so no lane can diff against a record that has
                // stopped describing the partition.
                return PartitionContentOwnership
                    .Observe(hub, PackageInstaller.TargetPartitionOf(pkg.Id, baseline), pkg.Id, logger)
                    .SelectMany(ownership => ownership.InstallerOwnsTheContent
                        ? Incremental(baseline)
                        : FullBecauseTheBaselineIsNotOurs(ownership));

                IObservable<InstallResult> FullBecauseTheBaselineIsNotOurs(
                    PartitionContentOwnershipVerdict ownership)
                {
                    logger?.LogWarning(
                        "Updating {Id}: its install record's {Files} declared file(s) cannot be used "
                        + "as a diff baseline — {Because}. Installing in FULL instead, so the record "
                        + "describes the partition again (MeshWeaver#4355).",
                        pkg.Id, baseline.InstalledFiles!.Count, ownership.Because);
                    return Full();
                }

                // Verified INSIDE the Catch, never around it: a fall-back to Full() is itself
                // verified, and wrapping the whole thing would report the landing twice.
                IObservable<InstallResult> Incremental(PackageManifest current) => Verified(WithModule(
                        IncrementalUpdate(hub, source, sourceRef, pkg, current, logger, authorizingUserId)))
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

    /// <summary>
    /// 🚨 <b>READ BACK WHAT THE INSTALL WROTE</b> — the half MeshWeaver#3485 left out.
    ///
    /// <para>#3485 added a completeness verdict, but only on the SKIP path: the module hash matched,
    /// so before believing "nothing to sync" the gate looked at the mesh. An install that actually
    /// WROTE was never compared against what landed — so a half-landed install stamps a record
    /// claiming every file, and nothing asks again until the module version moves. Measured on
    /// memex.meshweaver.cloud 2026-09-13: <c>Plugins/Hosting</c> stamped a 224-file record at
    /// 22:03:03Z; <c>Hosting/Deployment/Source/TriageIntake.cs</c> and both
    /// <c>Hosting/TriageStatus/Source/*.cs</c> never arrived; eight Hosting NodeTypes sat at
    /// <c>compilationStatus: Error</c> with <c>MISSING SOURCES: N of N</c> twelve hours later, and
    /// the only trace anywhere was the compiler's complaint about a type it could not find.</para>
    ///
    /// <para>One record read plus one batched <see cref="IStorageAdapter.ReadMany"/> over a bounded,
    /// KNOWN set — the same cost the pre-check already pays, and only on the paths that wrote.</para>
    ///
    /// <para>🚨 It can never fail the install it is verifying. An install that landed and a check
    /// that could not run are different facts, and collapsing them would make the check a new way
    /// for a working install to fail. A check that could not run says so, at Warning, and is
    /// explicitly not a pass.</para>
    /// </summary>
    /// <param name="hub">The hub the install ran on.</param>
    /// <param name="pkg">The package that was installed.</param>
    /// <param name="logger">Where the outcome is reported.</param>
    /// <returns>A cold observable emitting exactly once, after the outcome has been reported.</returns>
    private static IObservable<InstallCompletenessVerdict?> VerifyLanded(
        IMessageHub hub, PackageManifest pkg, ILogger? logger)
    {
        var persistence = hub.ServiceProvider.GetService<IStorageAdapter>();
        // Nothing to compare: a package that installs no nodes declares no node paths, and a host
        // with no storage adapter cannot read any. Silent — the pre-check is silent here too, and a
        // line per module-only package on every boot would be noise with no reader.
        if (persistence is null || pkg.Kind != PackageKind.NodeRepo)
            return Observable.Return<InstallCompletenessVerdict?>(null);

        var recordPath = $"{PackageInstaller.InstalledPartition}/{pkg.Id}";
        return persistence.Read(recordPath, hub.JsonSerializerOptions)
            .Take(1)
            // 🚨 DefaultIfEmpty, and it is load-bearing. A read that COMPLETES WITHOUT EMITTING
            // would make the SelectMany below produce nothing, and the caller's install result —
            // which this method only passes through — would be swallowed. That is a worse failure
            // than the fault this method already catches: the install would have run and its
            // observable would simply never emit. An absent record is a value here (null → the
            // Undeclared verdict, reported at Warning as "NOT verified"), never a silence.
            .DefaultIfEmpty()
            .Select(n => n?.ContentAs<PackageManifest>(hub.JsonSerializerOptions))
            // A record that cannot be read yields the Undeclared verdict below, which reports at
            // Warning as "NOT verified" — never as a pass, and never as a shortfall.
            .Catch<PackageManifest?, Exception>(_ => Observable.Return<PackageManifest?>(null))
            .SelectMany(record => InstallCompleteness.Observe(
                persistence, hub.JsonSerializerOptions, pkg.Id,
                // No record read ⇒ fall back to the CANDIDATE's own target partition. The verdict
                // is Undeclared either way (there is no file map to compare), but a partition
                // string still has to be a real one for the line to name where it looked.
                PackageInstaller.TargetPartitionOf(pkg.Id, record ?? pkg), record,
                // The INSTALL's own parser registry — the declared population must be the files
                // this install would write, never a superset (#3659).
                new FileFormatParserRegistry(
                    hub.JsonSerializerOptions,
                    hub.ServiceProvider.GetServices<IFileFormatParser>()),
                recordPath))
            .Do(verdict =>
            {
                var report = InstallCompleteness.DescribeLanding(verdict, pkg.ModuleVersion);
                logger?.Log(report.Level, "{Landing}", report.Message);
            })
            .Select(verdict => (InstallCompletenessVerdict?)verdict)
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex,
                    "Package {Id} installed, but the post-install completeness check could NOT "
                    + "run: {Cause}. This is not a pass — nothing here says the install landed "
                    + "whole (MeshWeaver#3485).", pkg.Id, ex.Message);
                return Observable.Return<InstallCompletenessVerdict?>(null);
            });
    }

    /// <summary>
    /// The up-to-date exit, once the mesh has actually been looked at (#3485). Kept as its own
    /// method so the three non-<see cref="InstallCompletenessKind.Complete"/> verdicts each get a
    /// distinct, greppable line instead of collapsing into one "nothing to sync".
    /// </summary>
    private static IObservable<InstallResult> SkipOrHeal(
        InstallCompletenessVerdict verdict,
        PackageManifest pkg,
        ILogger? logger,
        Func<IObservable<InstallResult>> full,
        Func<IObservable<InstallResult>, IObservable<InstallResult>> withModule)
    {
        if (verdict.Kind is InstallCompletenessKind.Incomplete)
        {
            // 🚨 WARNING, NOT ERROR — and the trade-off is deliberate (MeshWeaver#2387). This line
            // describes a DETECTION followed immediately by a repair. Logged at Error it re-opened
            // #2387 — an issue about the [DefaultInstall] summary line in this same class — through
            // the watcher's per-CATEGORY incident fold, on every boot. The Error now sits on the
            // OUTCOME (VerifyLanded → InstallCompleteness.DescribeLanding), which fires when the
            // repair did NOT restore the nodes.
            //
            // 🚨 But that outcome is read right after the write, so it proves the write LANDED,
            // never that it HELD. Measured on memex.meshweaver.cloud 2026-09-16:
            // Feedback/Feedback/Source/FeedbackHandover was named here on ELEVEN boots at one module
            // version — written back each time (22:02:45Z on 09-13 was the first), pruned each time
            // by Feedback/_GitSync importing the sealed commit whose tree lacks it (#4259's two
            // writers; that image predated #4292). A detection that REPEATS at an unchanged module
            // version is a repair that did not hold, and on this path no line above Warning says so
            // — Doc/Architecture/LogWatchTriage, "A REOPEN is not a recurrence".
            logger?.LogWarning(
                "Package {Id} records module {ModuleVersion} as installed, but {Missing} of "
                + "{Declared} declared node(s) are ABSENT from the mesh: [{Paths}]. Counted over: "
                + "{Population}. The content hash cannot see this — it describes the SOURCE, not "
                + "what landed — so the install is being REPAIRED rather than skipped. Whether the "
                + "repair worked is reported separately, once it has (MeshWeaver#3485).",
                pkg.Id, pkg.ModuleVersion, verdict.Missing.Count, verdict.Declared,
                string.Join(", ", verdict.Missing.Take(InstallCompleteness.MaxNamedInALine)),
                verdict.Population);
            return full();
        }

        // 🚨 MeshWeaver#4812 — a lane that reaches THIS exit is ASSERTING the package (the boot
        // baseline, an environment flag, a human's Install/Update click): whoever deleted the
        // partition, the caller wants the package installed, so a torn-down partition is healed
        // like an incomplete one. The boot REPAIR pass is the one caller that must not — it
        // filters TornDown out before it ever gets here (InstalledPackageRepairService.Heal).
        if (verdict.Kind is InstallCompletenessKind.TornDown)
        {
            logger?.LogWarning(
                "Package {Id} records module {ModuleVersion} as installed, but its partition was "
                + "torn down after that install: {Because}. This lane asserts the package, so it "
                + "is being REINSTALLED in full; whether that landed is reported separately "
                + "(MeshWeaver#4812).",
                pkg.Id, pkg.ModuleVersion, verdict.Because);
            return full();
        }

        if (verdict.Kind is InstallCompletenessKind.Complete)
            logger?.LogInformation(
                "Package {Id} content is up to date (module {ModuleVersion}, {Present}/{Declared} "
                + "declared node(s) present; counted over: {Population}); nothing to sync.",
                pkg.Id, pkg.ModuleVersion, verdict.Present, verdict.Declared, verdict.Population);
        else
            logger?.LogWarning(
                "Package {Id} content is up to date by module hash ({ModuleVersion}) but its "
                + "completeness was NOT verified ({Kind}): {Because}. Nothing is being reinstalled "
                + "— but this install has not been shown to be whole (MeshWeaver#3485).",
                pkg.Id, pkg.ModuleVersion, verdict.Kind, verdict.Because);

        // 🚨 #2417 — WithModule, and the missing wrapper here is half of why a package
        // could record as installed with no binary anywhere. This early return is a
        // CONTENT verdict: the manifest hash the record was stamped from equals the
        // one the source serves, so no node needs to travel. It says nothing whatever
        // about the module — and by returning unwrapped (the other two exits are
        // both WithModule'd) it made the content answer stand in for the module
        // answer. Once a moduleVersion was stamped, no install and no reconcile would
        // ever ask about the binary again, on any deployment.
        //
        // The module lane costs nothing when there is nothing to ask: WithModule is
        // the identity for a package declaring no module or a source that serves no
        // bundles, and AdoptModule absorbs every failure into a logged zero — its
        // presence-aware ModuleUpdateDecision answers SkipUpToDate for the normal
        // case, in which nothing travels either.
        return withModule(Observable.Return(new InstallResult(0, 0)));
    }

    /// <summary>
    /// The manifest-diff fast path: fetch only <c>manifest.lock</c>, diff, fetch only the changed
    /// CONTENT files — <b>plus the declared files whose node is ABSENT from the mesh</b>
    /// (MeshWeaver#4259). A changed module source (<c>src/&lt;Module&gt;/…</c>) is never fetched:
    /// it travels compiled, in the module bundle, and the content source does not serve it
    /// (MeshWeaver#4429).
    ///
    /// <para>🚨 <b>The diff alone is a comparison of two DECLARATIONS.</b>
    /// <c>newManifest.DiffFrom(record.InstalledFiles)</c> asks what the source changed since the
    /// record the installer itself stamped; nothing in it observes the mesh. A node lost AFTER a
    /// previous install therefore has an unchanged hash, never enters
    /// <c>delta.AddedOrChangedFiles</c>, is never fetched, and never reaches
    /// <c>PackageInstaller.DecideAndWrite</c> — which would have restored it, because it writes
    /// whenever <c>current is null</c>. The presence-awareness sits one layer below a set the absent
    /// file never enters.</para>
    ///
    /// <para>🚨 <b>And only the hash-EQUAL path could heal it.</b> #3485 made the up-to-date exit
    /// observe the mesh before skipping, so a reinstall at an unchanged module hash repairs. But an
    /// UPDATE never takes that exit, and every new publication moves the hash again — so a package
    /// that loses a node stays short across arbitrarily many updates, each reporting success.
    /// Measured on memex.meshweaver.cloud: <c>Plugins/Hosting</c> took this path at
    /// 2026-09-13T22:03Z and wrote exactly the two files whose content had moved
    /// (<c>…/Source/AksOpsResult</c> v1 22:02:54.601Z, <c>…/Source/PlatformBuildInboxWatcher</c> v16
    /// 22:02:57.255Z) while eleven declared-and-absent files stayed absent — among them the three
    /// the release node <c>Hosting/TriageStatus/Release/20260913081235-bylQeIj_</c> had compiled
    /// from that same morning.</para>
    ///
    /// <para>The repair costs ONE batched <see cref="IStorageAdapter.ReadMany"/> over the candidate
    /// manifest's own declared node paths — the same read <see cref="VerifyLanded"/> already pays on
    /// this exit, moved to where it can still change the outcome instead of only reporting it. A
    /// read that could not run does NOT widen the fetch and says so at Warning: an unobserved mesh
    /// is not a clean one, and re-fetching a whole package on every read hiccup would be a new cost
    /// nobody asked for.</para>
    /// </summary>
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

                // 🚨 #4429 — A MODULE SOURCE IS A CHANGE TOKEN, NEVER A FILE TO FETCH.
                // gen-manifests.py folds a mixed package's `src/<Module>/…` — and the in-tree
                // siblings that ride its bundle — into the SAME `files` map as its node files, so a
                // source-only commit moves the module version (Plugins#878/#1118). Those files are
                // compiled into the module bundle and arrive through the module lane (WithModule →
                // AdoptModule); the content source serves only `{Id}/…` (NodeRepoPackageSource keeps
                // the package folder, and the registry's /files answers from it). Asked for one, it
                // can only ever return 0 of N — which EnsureFetchComplete, correctly, refuses — so
                // every update that touched a source fell back to a FULL install and recompiled every
                // type in the package: `Edu` on memex.systemorph.com, 2026-09-15T14:12Z, for
                // `src/MeshWeaver.Courses/CourseAssetService.cs`. They stay in the record
                // (InstallNodeRepoDelta stamps newManifest.Files whole) so the next diff is clean;
                // they never enter the fetch. The ONE predicate the node mapping, the delta prune and
                // InstallCompleteness already share (#4101).
                var changedContent = delta.AddedOrChangedFiles
                    .Where(f => !PackageInstaller.IsModuleSourcePath(f))
                    .ToImmutableSortedSet(StringComparer.Ordinal);
                var changedModuleSources = delta.AddedOrChangedFiles.Count - changedContent.Count;

                // A change to the package's SHARED Source/Test (partition-level compile inputs)
                // affects every type in the package — the full install's release-all handles that;
                // the delta's owner-derivation would miss siblings.
                var sharedPrefixes = new[] { $"{pkg.Id}/Source/", $"{pkg.Id}/Test/" };
                if (delta.AddedOrChangedFiles.Concat(delta.RemovedFiles)
                    .Any(p => sharedPrefixes.Any(s => p.StartsWith(s, StringComparison.Ordinal))))
                    throw new InvalidOperationException(
                        $"Package '{pkg.Id}' changed shared Source/Test files; full install required.");

                // The install's OWN parser registry — the file→node rule is DI-dependent (#3659),
                // so a file whose extension no parser claims is not a node here either and must
                // neither be fetched as one nor pruned as one.
                var parsers = new FileFormatParserRegistry(
                    hub.JsonSerializerOptions, hub.ServiceProvider.GetServices<IFileFormatParser>());
                var changedNodePaths = delta.AddedOrChangedFiles
                    .Select(f => PackageInstaller.NodePathForFile(f, parsers))
                    .Where(p => p is not null).Select(p => p!)
                    .ToHashSet(StringComparer.Ordinal);
                // Removed FILES prune their nodes — unless the node is still fed by a changed file
                // (the `X.json` → `X/index.json` layout move maps both to node X).
                var removedNodePaths = delta.RemovedFiles
                    .Select(f => PackageInstaller.NodePathForFile(f, parsers))
                    .Where(p => p is not null && !changedNodePaths.Contains(p))
                    .Select(p => p!)
                    .ToHashSet(StringComparer.Ordinal);

                // 🚨 #4259 — the diff is a statement about the SOURCE and the RECORD; ask the MESH
                // too. The declared node paths of the candidate manifest are a bounded, KNOWN set,
                // so this is one batched ReadMany, never a query (a stale negative here would
                // re-fetch a file that is present) and never N point reads of possibly-absent paths
                // (which is what opens a storm breaker on the owning hub).
                var declaredNodePaths = newManifest.Files.Keys
                    .Select(f => PackageInstaller.NodePathForFile(f, parsers))
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(p => p!)
                    .ToImmutableSortedSet(StringComparer.Ordinal);

                // 🚨 COST, stated rather than hidden: this preflight is a SECOND full-manifest
                // ReadMany per incremental update — VerifyLanded still runs its own after
                // InstallNodeRepoDelta. The two are not redundant and neither can be dropped: this
                // one asks "which declared nodes are ABSENT so the delta must re-fetch them"
                // BEFORE the write (the whole of #4259), and VerifyLanded asks "did what we just
                // wrote land" AFTER it. Collapsing them would mean either re-fetching on a verdict
                // computed before the install, or discovering a lost node only on the NEXT update.
                // The added cost is one ReadMany over declaredNodePaths — bounded by the manifest,
                // not by mesh size — and it is paid only on the incremental path, which exists to
                // avoid re-fetching whole packages.
                return InstallCompleteness
                    .ObservePresent(
                        hub.ServiceProvider.GetService<IStorageAdapter>(),
                        hub.JsonSerializerOptions, declaredNodePaths)
                    .SelectMany(present =>
                    {
                        // 🚨 #3659 — a file the install itself could not read as a node is
                        // PERMANENTLY node-less, so widening the fetch for it would re-fetch,
                        // re-parse and re-skip it on every update forever, under a line that calls
                        // it an absent node being restored. A file whose hash MOVED is in
                        // `changedContent` and travels regardless, so a fixed one is still
                        // re-examined and drops out of the record.
                        var restore = InstallCompleteness.FilesToRestore(
                            newManifest.Files, changedContent, present, parsers,
                            record.UnreadableFiles);

                        // 🚨 The SAME rule the changed-file guard above applies, and for the same
                        // reason: a package's shared Source/Test are compile inputs for EVERY type
                        // in it, so the delta's owner-derivation would restore the file and
                        // recompile nothing but its nominal owner. An absent shared source is
                        // therefore a full install, whose release-all covers the siblings — the
                        // caller catches this and falls back, exactly as it does for a changed one.
                        if (restore.Any(p => sharedPrefixes.Any(
                                s => p.StartsWith(s, StringComparison.Ordinal))))
                            throw new InvalidOperationException(
                                $"Package '{pkg.Id}' is missing shared Source/Test node(s) "
                                + $"([{string.Join(", ", restore.Take(InstallCompleteness.MaxNamedInALine))}]); "
                                + "full install required.");

                        if (present is null)
                            logger?.LogWarning(
                                "Updating {Id} incrementally: the mesh could NOT be read, so the "
                                + "{Declared} declared node(s) were not checked for absence. This "
                                + "is not a pass — a node lost since the last install will not be "
                                + "restored by this update (MeshWeaver#4259).",
                                pkg.Id, declaredNodePaths.Count);
                        else if (restore.Count > 0)
                            logger?.LogWarning(
                                "Updating {Id} incrementally: {Restore} of {Declared} declared "
                                + "node(s) are ABSENT from the mesh although their file hash has "
                                + "not moved, so the diff alone would have skipped them. Re-fetching "
                                + "them: [{Files}] (MeshWeaver#4259).",
                                pkg.Id, restore.Count, declaredNodePaths.Count,
                                string.Join(", ", restore.Take(InstallCompleteness.MaxNamedInALine)));

                        var wanted = changedContent.Union(restore);
                        logger?.LogInformation(
                            "Updating {Id} incrementally: {Changed} changed content file(s), "
                            + "{Restored} restored, {Removed} removed; {ModuleSources} changed module "
                            + "source(s) travel in the module bundle, not the fetch → module "
                            + "{ModuleVersion}.",
                            pkg.Id, changedContent.Count, restore.Count,
                            delta.RemovedFiles.Count, changedModuleSources, newManifest.ModuleVersion);

                        return (wanted.Count == 0
                                ? Observable.Return((IReadOnlyList<PackageFile>)[])
                                : source.FetchPackageFiles(pkg, sourceRef, wanted))
                            .Do(fetched => EnsureFetchComplete(pkg.Id, wanted, fetched, logger))
                            .SelectMany(changedFiles => PackageInstaller.InstallNodeRepoDelta(
                                hub, pkg, newManifest, changedFiles, removedNodePaths, sourceRef,
                                logger, authorizingUserId));
                    });
            });
    }

    /// <summary>
    /// 🚨 <b>A file the fetch was ASKED for and did not get back is a shortfall with no other
    /// voice.</b> <see cref="IPackageSource.FetchPackageFiles(PackageManifest, string, IReadOnlyCollection{string})"/>
    /// filters — locally in the interface default, server-side in
    /// <see cref="RegistryPackageSource"/> — and its contract is explicit that *"paths absent from
    /// the package simply don't appear in the result"*. So a requested path the source does not
    /// serve (a serving-side casing difference, a stale registry cache, a bundle that disagrees with
    /// its own lock) vanishes with no exception and no line, the delta installs whatever did arrive,
    /// and <c>WriteInstalledRecord</c> then stamps the FULL declared map — a record asserting a file
    /// nothing ever wrote.
    ///
    /// <para>🚨 <b>It fails the incremental update rather than reporting it</b>, and the earlier
    /// "reports only" reading of this method was wrong. The objection to failing — that the install
    /// which did run is a fact, and collapsing "landed short" into "failed" makes a working update a
    /// new way to break — does not survive what happens NEXT: <c>InstallNodeRepoDelta</c> stamps
    /// <c>newManifest</c> as the next <c>InstalledFiles</c> baseline unconditionally, so a file that
    /// never travelled while its OLD node is still present leaves <see cref="VerifyLanded"/> seeing
    /// a present node and reporting completeness. The record then claims the NEW hash over OLD
    /// content, and every later update of that module diffs clean and skips it — permanently. The
    /// failure is not "landed short", it is "landed short and then lied about it".</para>
    ///
    /// <para>Nor does throwing cost the user the update. This is the same mechanism the
    /// absent-shared-source arm uses a few lines above: the caller catches it and falls back to a
    /// FULL install, which rewrites everything and writes a record that is true. The user gets an
    /// updated package either way; only the silent-stale path is removed.</para>
    ///
    /// <para>🚨 <b>A module source never reaches <paramref name="wanted"/></b> (MeshWeaver#4429).
    /// The lock declares a mixed package's <c>src/&lt;Module&gt;/…</c> beside its node files, but
    /// they travel in the module bundle and no content source serves them; the caller drops them
    /// with <see cref="PackageInstaller.IsModuleSourcePath"/> before it asks. Until it did, this
    /// guard turned every update that touched a module source into a full install. So a shortfall
    /// here is always a file the source SHOULD have served.</para>
    /// </summary>
    /// <param name="packageId">The package being updated.</param>
    /// <param name="wanted">The paths the fetch asked for.</param>
    /// <param name="fetched">What came back.</param>
    /// <param name="logger">Where the shortfall is named.</param>
    // Internal for the FetchShortfallFailsClosedTest pin (InternalsVisibleTo): a guard that only
    // the incremental-update path can reach is a guard nothing can falsify.
    internal static void EnsureFetchComplete(
        string packageId, IReadOnlySet<string> wanted, IReadOnlyList<PackageFile> fetched,
        ILogger? logger)
    {
        // 🚨 NOT gated on the logger. Computing the shortfall only when someone is listening makes
        // the guard disappear exactly where diagnostics are off, which is where a silent stale
        // record is least likely to be noticed.
        if (wanted.Count == 0)
            return;
        var missing = wanted
            .Except(fetched.Select(f => f.RelativePath), StringComparer.Ordinal)
            .ToImmutableSortedSet(StringComparer.Ordinal);
        if (missing.Count == 0)
            return;
        logger?.LogError(
            "Updating {Id} incrementally: the source returned {Fetched} of the {Wanted} file(s) "
            + "asked for — [{Missing}] did NOT travel, so their nodes cannot be written. Falling "
            + "back to a full install rather than stamping a record that declares them "
            + "(MeshWeaver#4259).",
            packageId, fetched.Count, wanted.Count,
            string.Join(", ", missing.Take(InstallCompleteness.MaxNamedInALine)));
        // 🚨 THROW, do not merely report. InstallNodeRepoDelta stamps `newManifest` as the next
        // InstalledFiles baseline unconditionally. If a requested file is missing while its OLD
        // node is still present, VerifyLanded sees a present node and reports completeness — so
        // the record would claim the new hash over old content, and the next update of this module
        // would diff clean and skip it FOREVER. A shortfall must therefore never reach the write.
        // The same mechanism the absent-shared-source arm above uses: the caller catches this and
        // falls back to a full install, whose release-all rewrites everything.
        throw new InvalidOperationException(
            $"Package '{packageId}' asked its source for {wanted.Count} file(s) and received "
            + $"{fetched.Count} ([{string.Join(", ", missing.Take(InstallCompleteness.MaxNamedInALine))}] "
            + "did not travel); full install required.");
    }

    private static ILogger? Logger(LayoutAreaHost host) =>
        host.Hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.PluginCatalog.Catalog");
}
