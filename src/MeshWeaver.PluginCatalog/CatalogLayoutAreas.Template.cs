using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Utils;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>The page-level values of the catalog template: the authored intro, the source line, and
/// which of the template's sections are showing (a style per section — the tree's SHAPE never depends
/// on data; Doc/GUI/DataBinding → "Templates first, data later").</summary>
public sealed record CatalogPageView
{
    /// <summary>The catalog node's authored description (markdown), or empty.</summary>
    public string Description { get; init; } = "";

    /// <summary>The description's style — hidden when there is none.</summary>
    public string DescriptionStyle { get; init; } = CatalogLayoutAreas.Hidden;

    /// <summary>"Source: … · N packages", or "no source configured".</summary>
    public string SourceLine { get; init; } = "";

    /// <summary>The source line's style — hidden while loading.</summary>
    public string SourceLineStyle { get; init; } = CatalogLayoutAreas.Hidden;

    /// <summary>The "loading the catalog" line's style — visible until the listing answers.</summary>
    public string LoadingStyle { get; init; } = "";

    /// <summary>The back-to-categories button's style — shown on a card page.</summary>
    public string BackStyle { get; init; } = CatalogLayoutAreas.Hidden;

    /// <summary>The card page's heading: the category, or "all packages".</summary>
    public string Heading { get; init; } = "";

    /// <summary>The heading's style — shown on a card page.</summary>
    public string HeadingStyle { get; init; } = CatalogLayoutAreas.Hidden;

    /// <summary>The empty-state message's style — shown when the page has nothing to list.</summary>
    public string EmptyStyle { get; init; } = CatalogLayoutAreas.Hidden;

    /// <summary>The category tiles' style — shown on the landing.</summary>
    public string TilesStyle { get; init; } = CatalogLayoutAreas.Hidden;

    /// <summary>The orphaned-record section heading's style — shown on the ALL page when there are orphans.</summary>
    public string OrphansStyle { get; init; } = CatalogLayoutAreas.Hidden;
}

/// <summary>One category tile of the landing. <see cref="Category"/> is the bucket key, null for the
/// all-packages tile; the click navigates to the href the SERVER builds from it.</summary>
public sealed record CatalogTileRow
{
    /// <summary>The bucket key the tile opens, or null for "all packages".</summary>
    public string? Category { get; init; }

    /// <summary>The tile's label, in the viewer's language for the localized buckets.</summary>
    public string Label { get; init; } = "";

    /// <summary>"N packages", localized.</summary>
    public string Count { get; init; } = "";
}

/// <summary>One package card of a card page — every line the card shows, and a style per optional line.
/// <see cref="Id"/> is the package's identity; the actions resolve the package from the SERVER's current
/// listing by it, never from the row's other fields (the row is user input).</summary>
public sealed record CatalogCardRow
{
    /// <summary>The package id — the card's identity.</summary>
    public string Id { get; init; } = "";

    /// <summary>The package's display name.</summary>
    public string Name { get; init; } = "";

    /// <summary>The package's description, or empty.</summary>
    public string Description { get; init; } = "";

    /// <summary>The description's style — hidden when there is none.</summary>
    public string DescriptionStyle { get; init; } = CatalogLayoutAreas.Hidden;

    /// <summary>"v1.2.3 · Kind · → Partition".</summary>
    public string Meta { get; init; } = "";

    /// <summary>The status line: installed, requires global admin, or a plan-tier refusal.</summary>
    public string Status { get; init; } = "";

    /// <summary>The status line's style — hidden when the card has no status line.</summary>
    public string StatusStyle { get; init; } = CatalogLayoutAreas.Hidden;

    /// <summary>"Install", or "Update to vX".</summary>
    public string InstallLabel { get; init; } = "";

    /// <summary>The Install / Update button's style — hidden when the card offers neither.</summary>
    public string InstallStyle { get; init; } = CatalogLayoutAreas.Hidden;

    /// <summary>The per-package update-policy row's style — a global administrator's control on an
    /// installed package.</summary>
    public string PolicyStyle { get; init; } = CatalogLayoutAreas.Hidden;

    /// <summary>The Auto choice's appearance — accented when it is the package's current policy.</summary>
    public string AutoAppearance { get; init; } = Appearance.Neutral;

    /// <summary>The Notify choice's appearance.</summary>
    public string NotifyAppearance { get; init; } = Appearance.Neutral;

    /// <summary>The Pinned (None) choice's appearance.</summary>
    public string NoneAppearance { get; init; } = Appearance.Neutral;

    /// <summary>The module's activation state on this process (restart required, quarantined, held,
    /// runs the image's copy, runs the previous version), or empty.</summary>
    public string Note { get; init; } = "";

    /// <summary>The activation note's style — hidden when there is none.</summary>
    public string NoteStyle { get; init; } = CatalogLayoutAreas.Hidden;

    /// <summary>What the module declares about its platform floor, beside its state, or empty.</summary>
    public string Floor { get; init; } = "";

    /// <summary>The floor advisory's style — hidden when there is none.</summary>
    public string FloorStyle { get; init; } = CatalogLayoutAreas.Hidden;
}

/// <summary>One orphaned install record — a record whose package the source no longer offers.</summary>
public sealed record CatalogOrphanRow
{
    /// <summary>The recorded package id — the row's identity.</summary>
    public string Id { get; init; } = "";

    /// <summary>The recorded display name.</summary>
    public string Name { get; init; } = "";

    /// <summary>"id · vX · → Partition".</summary>
    public string Meta { get; init; } = "";

    /// <summary>The Remove button's style — a global administrator's action.</summary>
    public string RemoveStyle { get; init; } = CatalogLayoutAreas.Hidden;

    /// <summary>The "requires global admin" hint's style — shown instead of Remove.</summary>
    public string AdminOnlyStyle { get; init; } = CatalogLayoutAreas.Hidden;
}

public static partial class CatalogLayoutAreas
{
    /// <summary>The style of a template section that is not showing.</summary>
    public const string Hidden = "display: none;";

    /// <summary>The <c>/data</c> id of the page-level values (<see cref="CatalogPageView"/>).</summary>
    public const string PageDataId = "catalogPage";

    /// <summary>The <c>/data</c> id of the landing's tiles (<see cref="CatalogTileRow"/>).</summary>
    public const string TilesDataId = "catalogTiles";

    /// <summary>The <c>/data</c> id of the card page's cards (<see cref="CatalogCardRow"/>).</summary>
    public const string CardsDataId = "catalogCards";

    /// <summary>The <c>/data</c> id of the ALL page's orphaned records (<see cref="CatalogOrphanRow"/>).</summary>
    public const string OrphansDataId = "catalogOrphans";

    /// <summary>
    /// What the SERVER knows about the page the person is looking at, as of its latest render — what a
    /// row-scoped action resolves the clicked row's identity against. The row says WHICH package; this
    /// says WHAT it is and whether the action is on offer for it, so a row forged or gone stale since
    /// the render can never install, re-pin or remove anything the page does not currently offer.
    /// </summary>
    /// <param name="Source">The package source, or null when none is configured.</param>
    /// <param name="SourceRef">The git ref the source lists at.</param>
    /// <param name="Available">Everything the source offers — the dependency-resolution universe.</param>
    /// <param name="KnownInstalled">Package ids already installed (skipped by the dependency closure).</param>
    /// <param name="Installable">The packages whose card currently offers Install / Update, by id.</param>
    /// <param name="Policies">The current update policy of each package whose card offers the policy row, by id.</param>
    /// <param name="RemovableOrphans">The orphaned records whose card currently offers Remove.</param>
    /// <param name="ViewerId">
    /// The viewer these offers were computed FOR — the id the global-admin flag behind
    /// <paramref name="Policies"/> and <paramref name="RemovableOrphans"/> was evaluated on. Null
    /// when nobody is signed in. 🚨 The two administrator actions run as System, so the action
    /// itself verifies the acting identity: it acts only when the identity acting is this viewer
    /// (<see cref="ClickedByThePagesViewer"/>), a platform administrator.
    /// </param>
    internal sealed record CatalogActionContext(
        IPackageSource? Source, string SourceRef, IReadOnlyList<PackageManifest> Available,
        IReadOnlySet<string> KnownInstalled, ImmutableDictionary<string, PackageManifest> Installable,
        ImmutableDictionary<string, PackageUpdatePolicy> Policies, ImmutableHashSet<string> RemovableOrphans,
        string? ViewerId = null)
    {
        /// <summary>Before anything has rendered: nothing is on offer.</summary>
        public static readonly CatalogActionContext None = new(
            null, "HEAD", [], ImmutableHashSet<string>.Empty,
            ImmutableDictionary<string, PackageManifest>.Empty,
            ImmutableDictionary<string, PackageUpdatePolicy>.Empty, ImmutableHashSet<string>.Empty);
    }

    /// <summary>One render's worth of data for the template: what it shows, and what its actions may act on.</summary>
    internal sealed record CatalogState(
        CatalogPageView Page, ImmutableList<CatalogTileRow> Tiles, ImmutableList<CatalogCardRow> Cards,
        ImmutableList<CatalogOrphanRow> Orphans, CatalogActionContext Actions);

    // ————————————————————————————————————————————— the template (reads nothing)

    /// <summary>
    /// The catalog page as a TEMPLATE (Doc/GUI/DataBinding → "Templates first, data later"): every
    /// section — the frame, the landing's tiles, a card page's cards and the orphan section — is
    /// declared at once and bound by pointer; <paramref name="feed"/> writes the values into
    /// <c>/data</c> and decides which sections show. The per-card buttons are ROW-SCOPED actions
    /// (Doc/GUI/DataBinding → "Row-scoped actions"): each is declared ONCE in its row template, and
    /// the click carries the row it was clicked in.
    /// </summary>
    /// <param name="host">The layout area host (localizes the static labels; runs the actions).</param>
    /// <param name="feed">The page's data, as it changes (<see cref="CatalogFeed"/> / <see cref="FeedFromSource"/>).</param>
    internal static UiControl CatalogTemplate(LayoutAreaHost host, IObservable<CatalogState> feed)
    {
        // The latest server-side view of the page, for the row-scoped actions to resolve against.
        // Owned by this render (disposed with its area), never shared beyond it.
        var current = new BehaviorSubject<CatalogActionContext>(CatalogActionContext.None);

        var pagePointer = LayoutAreaReference.GetDataPointer(PageDataId);
        UiControl Page(UiControl control) => control with { DataContext = pagePointer };

        var tiles = Rows(TilesDataId, TileTemplate())
            .WithStyle("display: grid; grid-template-columns: repeat(auto-fill, minmax(210px, 1fr)); "
                       + "gap: 14px; margin: 12px 0; width: 100%;");

        return Controls.Stack
            .WithWidth("100%")
            .WithStyle("width: 100%; max-width: 900px; margin: 0 auto; padding: 16px;")
            .WithView(Controls.H1(host.Localize("ui.pluginCatalog")).WithStyle("margin: 0 0 4px 0;"), "title")
            .WithView(Page(Controls.Markdown(Bound(nameof(CatalogPageView.Description)))
                with { Style = Bound(nameof(CatalogPageView.DescriptionStyle)) }), "description")
            .WithView(Page(Controls.Body(Bound(nameof(CatalogPageView.SourceLine)))
                with { Style = Bound(nameof(CatalogPageView.SourceLineStyle)) }), "source")
            .WithView(Page(Controls.Markdown(host.Localize("ui.mdLoadingCatalog"))
                with { Style = Bound(nameof(CatalogPageView.LoadingStyle)) }), "loading")
            .WithView(Page(Controls.Button(host.Localize("ui.catalogBackToCategories"))
                    .WithClickAction(ctx =>
                    {
                        ctx.NavigateTo(LandingHref(host.Hub.Address));
                        return Task.CompletedTask;
                    })
                with { Style = Bound(nameof(CatalogPageView.BackStyle)) }), "back")
            .WithView(Page(Controls.H2(Bound(nameof(CatalogPageView.Heading)))
                with { Style = Bound(nameof(CatalogPageView.HeadingStyle)) }), "heading")
            .WithView(Page(Controls.Markdown(host.Localize("ui.mdNoPackages"))
                with { Style = Bound(nameof(CatalogPageView.EmptyStyle)) }), "empty")
            .WithView(Page(Controls.Stack.WithView(tiles, "grid")
                with { Style = Bound(nameof(CatalogPageView.TilesStyle)) }), "categories")
            .WithView(Rows(CardsDataId, CardTemplate(host, current)).WithStyle("width: 100%;"), "cards")
            .WithView(Page(Controls.Stack
                    .WithView(Controls.H2(host.Localize("ui.orphanedInstallRecords"))
                        .WithStyle("margin: 24px 0 4px 0;"), "heading")
                    .WithView(Controls.Markdown(host.Localize("ui.mdOrphanedInstallRecords"))
                        .WithStyle("margin-bottom: 8px;"), "hint")
                with { Style = Bound(nameof(CatalogPageView.OrphansStyle)) }), "orphanHeader")
            .WithView(Rows(OrphansDataId, OrphanTemplate(host, current)).WithStyle("width: 100%;"), "orphans")
            .WithBuildup((h, context, store) =>
            {
                h.RegisterForDisposal(context.Area, feed.Subscribe(
                        state =>
                        {
                            current.OnNext(state.Actions);
                            h.UpdateData(PageDataId, state.Page);
                            h.UpdateData(TilesDataId, state.Tiles);
                            h.UpdateData(CardsDataId, state.Cards);
                            h.UpdateData(OrphansDataId, state.Orphans);
                        },
                        ex => Logger(h)?.LogWarning(ex, "Catalog in area {Area}: its feed failed", context.Area)));
                return new(store, [], null);
            });
    }

    // A pointer to a property of the bound record, as the serializer spells it.
    private static JsonPointerReference Bound(string property) => new(property.ToCamelCase() ?? property);

    // A bound row list: ONE view, rendered by the client once per element of /data/{id}.
    private static ItemTemplateControl Rows(string id, UiControl view)
        => new(view with { DataContext = "/" }, new JsonPointerReference(""))
        {
            DataContext = LayoutAreaReference.GetDataPointer(id),
        };

    // One tile: its label and count, bound to the row; the click opens the row's category.
    private static UiControl TileTemplate()
        => Controls.Stack
            .WithStyle("cursor: pointer; border: 1px solid var(--neutral-stroke-rest); border-radius: 12px; "
                       + "padding: 16px; min-height: 92px; background: var(--neutral-layer-1); "
                       + "display: flex; flex-direction: column; justify-content: space-between; gap: 6px;")
            .WithView(Controls.Body(Bound(nameof(CatalogTileRow.Label)))
                .WithStyle("font-weight: 700; font-size: 1.05rem; display: block;"), "name")
            .WithView(Controls.Body(Bound(nameof(CatalogTileRow.Count)))
                .WithStyle("color: var(--neutral-foreground-hint); font-size: 0.85rem; display: block;"), "count")
            .WithClickAction(OpenTile);

    // One package card: every line bound to the row; the Install / Update button and the three
    // policy choices are row-scoped actions.
    private static UiControl CardTemplate(LayoutAreaHost host, BehaviorSubject<CatalogActionContext> current)
    {
        UiControl Choice(PackageUpdatePolicy policy, string key, string appearance) =>
            Controls.Button(host.Localize(key))
                .WithAppearance(Bound(appearance))
                .WithClickAction(ctx => SetPolicyClicked(ctx, current.Value, policy));

        var policyRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap(6)
            .WithStyle("align-items: center; margin-top: 8px;")
            .WithView(Controls.Body(host.Localize("ui.catalogUpdatePolicy"))
                .WithStyle("font-size: 12px; color: var(--neutral-foreground-hint); margin-right: 4px;"), "label")
            .WithView(Choice(PackageUpdatePolicy.Auto, "ui.catalogUpdatePolicyAuto", nameof(CatalogCardRow.AutoAppearance)), "auto")
            .WithView(Choice(PackageUpdatePolicy.Notify, "ui.catalogUpdatePolicyNotify", nameof(CatalogCardRow.NotifyAppearance)), "notify")
            .WithView(Choice(PackageUpdatePolicy.None, "ui.catalogUpdatePolicyNone", nameof(CatalogCardRow.NoneAppearance)), "none");

        return Controls.Stack
            .WithWidth("100%")
            .WithStyle("border: 1px solid var(--neutral-stroke-rest); border-radius: 8px; "
                       + "padding: 14px 16px; margin-bottom: 12px;")
            .WithView(Controls.Body(Bound(nameof(CatalogCardRow.Name)))
                .WithStyle("font-weight: 600; font-size: 16px; display: block; margin-bottom: 4px;"), "name")
            .WithView(Controls.Body(Bound(nameof(CatalogCardRow.Description)))
                with { Style = Bound(nameof(CatalogCardRow.DescriptionStyle)) }, "description")
            .WithView(Controls.Body(Bound(nameof(CatalogCardRow.Meta)))
                .WithStyle("color: var(--neutral-foreground-hint); font-size: 12px; display: block; margin-bottom: 10px;"), "meta")
            .WithView(Controls.Body(Bound(nameof(CatalogCardRow.Status)))
                with { Style = Bound(nameof(CatalogCardRow.StatusStyle)) }, "status")
            .WithView(Controls.Button(Bound(nameof(CatalogCardRow.InstallLabel)))
                .WithAppearance(Appearance.Accent)
                .WithClickAction(ctx => InstallClicked(host, ctx, current.Value))
                with { Style = Bound(nameof(CatalogCardRow.InstallStyle)) }, "install")
            .WithView(Controls.Stack
                    .WithView(policyRow, "choices")
                    .WithView(Controls.Body(host.Localize("ui.catalogUpdatePolicyHint"))
                        .WithStyle("font-size: 11px; color: var(--neutral-foreground-hint); display: block; margin-top: 2px;"), "hint")
                with { Style = Bound(nameof(CatalogCardRow.PolicyStyle)) }, "updatePolicy")
            .WithView(Controls.Body(Bound(nameof(CatalogCardRow.Note)))
                with { Style = Bound(nameof(CatalogCardRow.NoteStyle)) }, "note")
            .WithView(Controls.Body(Bound(nameof(CatalogCardRow.Floor)))
                with { Style = Bound(nameof(CatalogCardRow.FloorStyle)) }, "floor");
    }

    // One orphaned record: what it says it is, and — for a global admin — its Remove action.
    private static UiControl OrphanTemplate(LayoutAreaHost host, BehaviorSubject<CatalogActionContext> current)
        => Controls.Stack
            .WithWidth("100%")
            .WithStyle("border: 1px dashed var(--neutral-stroke-rest); border-radius: 8px; "
                       + "padding: 14px 16px; margin-bottom: 12px;")
            .WithView(Controls.Body(Bound(nameof(CatalogOrphanRow.Name)))
                .WithStyle("font-weight: 600; font-size: 16px; display: block; margin-bottom: 4px;"), "name")
            .WithView(Controls.Body(Bound(nameof(CatalogOrphanRow.Meta)))
                .WithStyle("color: var(--neutral-foreground-hint); font-size: 12px; display: block; margin-bottom: 10px;"), "meta")
            .WithView(Controls.Body(host.Localize("ui.requiresGlobalAdmin"))
                with { Style = Bound(nameof(CatalogOrphanRow.AdminOnlyStyle)) }, "adminOnly")
            .WithView(Controls.Button(host.Localize("ui.removeInstallRecord"))
                .WithClickAction(ctx => RemoveClicked(host, ctx, current.Value))
                with { Style = Bound(nameof(CatalogOrphanRow.RemoveStyle)) }, "remove");

    // ————————————————————————————————————————————— the row-scoped actions

    /// <summary>Opens the clicked tile's category — the href is built HERE from the row's key, so a
    /// tile row names a category and nothing else.</summary>
    internal static Task OpenTile(UiActionContext ctx)
    {
        if (ctx.RowAs<CatalogTileRow>() is { } tile)
            ctx.NavigateTo(tile.Category is { Length: > 0 } category
                ? CategoryHref(ctx.Host.Hub.Address, category)
                : AllHref(ctx.Host.Hub.Address));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Install / Update on the card the click came from. The row names the package; the package
    /// itself — and whether its card offers the action at all — comes from the server's current page
    /// (<paramref name="page"/>), so a row from before a refresh installs exactly the package that was
    /// clicked, and a package that has since left the listing or been installed installs nothing.
    /// </summary>
    internal static Task InstallClicked(LayoutAreaHost host, UiActionContext ctx, CatalogActionContext page)
    {
        if (ctx.RowAs<CatalogCardRow>() is not { Id.Length: > 0 } row)
            return Task.CompletedTask;
        if (page.Source is null || !page.Installable.TryGetValue(row.Id, out var pkg))
        {
            Logger(host)?.LogInformation(
                "Catalog: Install clicked on {Id}, which the current page does not offer to install.", row.Id);
            return Task.CompletedTask;
        }
        InstallPackage(host, page.Source, page.SourceRef, pkg, page.Available, page.KnownInstalled);
        return Task.CompletedTask;
    }

    /// <summary>Sets the clicked card's package update policy — only on a card that currently offers
    /// the policy row (an installed package, a global administrator viewing), and only when it changes.
    /// The click authorizes; the write runs as System (<see cref="PackageInstaller.SetUpdatePolicy"/>).</summary>
    internal static Task SetPolicyClicked(UiActionContext ctx, CatalogActionContext page, PackageUpdatePolicy policy)
    {
        if (ctx.RowAs<CatalogCardRow>() is not { Id.Length: > 0 } row
            || !page.Policies.TryGetValue(row.Id, out var currentPolicy)
            || currentPolicy == policy)
            return Task.CompletedTask;
        var logger = Logger(ctx.Host);
        if (!ClickedByThePagesViewer(ctx.Host, page))
        {
            logger?.LogWarning(
                "Catalog: update-policy click on {Id} refused — it was not made by the viewer this page was rendered for.",
                row.Id);
            return Task.CompletedTask;
        }
        PackageInstaller.SetUpdatePolicy(ctx.Hub, row.Id, policy, logger)
            .Subscribe(
                _ => { },
                ex => logger?.LogWarning(ex, "Setting update policy {Policy} on {Id} failed.", policy, row.Id));
        return Task.CompletedTask;
    }

    /// <summary>Removes the clicked orphaned record — only one the current page lists as removable
    /// (an orphan, a global administrator viewing).</summary>
    internal static Task RemoveClicked(LayoutAreaHost host, UiActionContext ctx, CatalogActionContext page)
    {
        if (ctx.RowAs<CatalogOrphanRow>() is not { Id.Length: > 0 } row || !page.RemovableOrphans.Contains(row.Id))
            return Task.CompletedTask;
        if (!ClickedByThePagesViewer(host, page))
        {
            Logger(host)?.LogWarning(
                "Catalog: Remove click on orphaned record {Id} refused — it was not made by the viewer this page was rendered for.",
                row.Id);
            return Task.CompletedTask;
        }
        RemoveInstallRecord(host, row.Id);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Whether the click being handled was made by the viewer <paramref name="page"/>'s offers were
    /// computed for. The click's identity is the ambient one of its delivery (the same reading
    /// <c>InstallPackage</c> takes as "who authorized the install"); the page's is
    /// <see cref="CatalogActionContext.ViewerId"/>.
    ///
    /// <para>🚨 The two administrator actions execute as System, so nothing downstream can refuse
    /// them: the action verifies, here, that the acting identity is the platform administrator the
    /// offers were computed for.</para>
    /// </summary>
    /// <param name="host">The layout area host handling the click.</param>
    /// <param name="page">The server's current view of the page.</param>
    /// <returns>True only when a signed-in viewer rendered the page and the same identity clicked.</returns>
    internal static bool ClickedByThePagesViewer(LayoutAreaHost host, CatalogActionContext page)
        => page.ViewerId is { Length: > 0 } viewer
           && string.Equals(ResolveViewerId(host), viewer, StringComparison.Ordinal);

    /// <summary>
    /// The viewer a page of this render is FOR: the identity of the subscription that opened the
    /// host (<see cref="LayoutAreaHost.ViewerContext"/>), which holds whichever thread the source's
    /// listing answers on; the ambient identity only when the subscription carried none.
    /// </summary>
    /// <param name="host">The layout area host.</param>
    /// <returns>The viewer's id, or null when nobody is signed in.</returns>
    internal static string? PageViewerId(LayoutAreaHost host)
        => host.ViewerContext?.ObjectId is { Length: > 0 } subscriber ? subscriber : ResolveViewerId(host);

    // ————————————————————————————————————————————— the feed (reads; builds no control)

    /// <summary>The catalog node's feed: the node's <see cref="PluginCatalogContent"/> picks the source,
    /// and <see cref="FeedFromSource"/> renders from it. A re-pointed catalog supersedes the old feed.</summary>
    internal static IObservable<CatalogState> CatalogFeed(LayoutAreaHost host)
        => host.Workspace.GetMeshNodeStream()
            .Select(node => node.ContentAs<PluginCatalogContent>(host.Hub.JsonSerializerOptions))
            .Select(cfg => FeedFromSource(
                host, BuildSource(host, cfg?.SourceRepoPath, cfg?.SourceSubdir, cfg?.Format),
                cfg?.SourceRef ?? "HEAD", cfg?.Description,
                cfg?.SourceRepoPath is { Length: > 0 } p ? $"{p} @ {cfg.SourceRef}" : null))
            .Switch()
            .StartWith(Loading);

    /// <summary>
    /// The page's data from an arbitrary <paramref name="source"/>. The source's listing decides the
    /// page (<see cref="Plan"/>): the landing is computed straight off it; a card page joins its cards
    /// against the install registry (live), the viewer's admin flag (live) and the restart-as-activation
    /// state (live).
    /// </summary>
    internal static IObservable<CatalogState> FeedFromSource(
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
                    return Observable.Return(Loading);
                var plan = Plan(requestedCategory, requestedAll, feed.Packages);
                if (plan.Kind == CatalogPage.Landing)
                    // The landing composes NOTHING beyond the listing it was built from: no install
                    // record, no permission evaluation, no activation-state read. That is the whole
                    // point of opening on categories.
                    return Observable.Return(ProjectLanding(host, source, description, sourceLabel, sourceRef, plan));
                // Resolved ONCE, here, and handed to both the admin flag and the offers it gates, so
                // the page can say whose offers they are (CatalogActionContext.ViewerId).
                var viewerId = PageViewerId(host);
                return ObserveInstalledFor(host, plan)
                    .CombineLatest(ObserveInstalledIds(host, plan), ObserveViewerIsGlobalAdmin(host, viewerId),
                        ObserveActivation(host),
                        (installed, installedIds, isAdmin, activation) => ProjectPackages(
                            host, source, sourceRef, description, sourceLabel, plan, installed, installedIds,
                            isAdmin, viewerId, activation));
            })
            // Switch, never SelectMany: a re-listing of the source supersedes the page built from
            // the previous listing instead of leaving two computations pushing into one view.
            .Switch();
    }

    /// <summary>Renders the catalog from <paramref name="source"/> — the template, fed by
    /// <see cref="FeedFromSource"/>. Shared by the node Overview's tests.</summary>
    internal static IObservable<UiControl?> RenderFromSource(
        LayoutAreaHost host, IPackageSource? source, string sourceRef, string? description, string? sourceLabel)
        => Observable.Return<UiControl?>(
            CatalogTemplate(host, FeedFromSource(host, source, sourceRef, description, sourceLabel)));

    // Before the source has answered: the loading line and nothing else.
    private static CatalogState Loading { get; } = new(
        new CatalogPageView(), [], [], [], CatalogActionContext.None);

    // ————————————————————————————————————————————— the projections (pure over their inputs)

    private static string Show(bool visible, string style) => visible ? style : Hidden;

    private static CatalogPageView Frame(
        LayoutAreaHost host, IPackageSource? source, string? description, string? sourceLabel, int total)
        => new()
        {
            Description = description ?? "",
            DescriptionStyle = Show(!string.IsNullOrWhiteSpace(description), "margin-bottom: 8px;"),
            SourceLine = source is null
                ? host.Localize("ui.catalogNoSource")
                : host.Localize("ui.catalogSourceSummary",
                    sourceLabel ?? host.Localize("ui.catalogRegistry"),
                    host.LocalizePlural("plural.package", total)),
            SourceLineStyle = "color: var(--neutral-foreground-hint); margin-bottom: 16px; display: block;",
            LoadingStyle = Hidden,
        };

    // The label a category key renders as: the source's own spelling, or the localized bucket name.
    private static string CategoryLabel(LayoutAreaHost host, string key) =>
        IsUncategorized(key) ? host.Localize("ui.catalogUncategorized") : key;

    // THE LANDING: one tile per category plus the all-packages entry — built from the listing alone.
    private static CatalogState ProjectLanding(
        LayoutAreaHost host, IPackageSource? source, string? description, string? sourceLabel, string sourceRef,
        CatalogPlan plan)
    {
        var page = Frame(host, source, description, sourceLabel, plan.Total) with
        {
            EmptyStyle = Show(plan.Total == 0, ""),
            TilesStyle = Show(plan.Total > 0, ""),
        };
        var tiles = plan.Total == 0
            ? ImmutableList<CatalogTileRow>.Empty
            : plan.Categories
                .Select(c => new CatalogTileRow
                {
                    Category = c.Key,
                    Label = CategoryLabel(host, c.Key),
                    Count = host.LocalizePlural("plural.package", c.Count),
                })
                .Append(new CatalogTileRow
                {
                    Label = host.Localize("ui.catalogAllPackages"),
                    Count = host.LocalizePlural("plural.package", plan.Total),
                })
                .ToImmutableList();
        return new(page, tiles, [], [], CatalogActionContext.None with { Source = source, SourceRef = sourceRef });
    }

    // A CARD page: one category's cards, or every card plus the orphan section on the ALL page.
    private static CatalogState ProjectPackages(
        LayoutAreaHost host, IPackageSource? source, string sourceRef, string? description, string? sourceLabel,
        CatalogPlan plan, IReadOnlyList<MeshNode> installed, ImmutableHashSet<string> installedIds,
        bool viewerIsGlobalAdmin, string? viewerId, ModuleActivationReport activation)
    {
        var installedById = installed
            .Select(n => n.ContentAs<PackageManifest>(host.Hub.JsonSerializerOptions))
            .OfType<PackageManifest>()
            .Where(m => !string.IsNullOrEmpty(m.Id))
            .GroupBy(m => m.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        // The whole listing + what is already installed are what a click needs to resolve the
        // package's dependency closure (PackageDependencyGraph.InstallClosure) — the listing is in
        // hand; the installed set is the shell listing plus the records this page read.
        var knownInstalled = installedIds.Union(installedById.Keys);

        var cards = ImmutableList.CreateBuilder<CatalogCardRow>();
        var installable = ImmutableDictionary.CreateBuilder<string, PackageManifest>(StringComparer.Ordinal);
        var policies = ImmutableDictionary.CreateBuilder<string, PackageUpdatePolicy>(StringComparer.Ordinal);
        foreach (var pkg in plan.Packages)
        {
            installedById.TryGetValue(pkg.Id, out var inst);
            var card = CardRow(host, source, pkg, inst, viewerIsGlobalAdmin, activation);
            cards.Add(card);
            if (card.InstallStyle != Hidden)
                installable[pkg.Id] = pkg;
            if (card.PolicyStyle != Hidden && inst is not null)
                policies[pkg.Id] = inst.EffectiveUpdatePolicy;
        }

        var orphans = plan.Kind == CatalogPage.All
            ? Orphaned(plan.Available, installed, host.Hub.JsonSerializerOptions)
            : [];
        var orphanRows = orphans
            .Select(o => new CatalogOrphanRow
            {
                Id = o.Id,
                Name = o.Name ?? o.Id,
                Meta = $"{o.Id}  ·  v{o.Version}  ·  → {o.TargetPartition ?? o.Id}",
                RemoveStyle = Show(viewerIsGlobalAdmin, ""),
                AdminOnlyStyle = Show(!viewerIsGlobalAdmin,
                    "color: var(--neutral-foreground-hint); font-size: 12px; display: block;"),
            })
            .ToImmutableList();

        var page = Frame(host, source, description, sourceLabel, plan.Total) with
        {
            BackStyle = "align-self: flex-start; margin: 0 0 8px 0;",
            Heading = plan.Kind == CatalogPage.All
                ? host.Localize("ui.catalogAllPackages")
                : CategoryLabel(host, plan.Category ?? Uncategorized),
            HeadingStyle = "margin: 8px 0 4px 0;",
            EmptyStyle = Show(plan.Packages.Count == 0, ""),
            OrphansStyle = Show(orphanRows.Count > 0, ""),
        };
        return new(page, [], cards.ToImmutable(), orphanRows,
            new CatalogActionContext(source, sourceRef, plan.Available, knownInstalled,
                installable.ToImmutable(), policies.ToImmutable(),
                viewerIsGlobalAdmin
                    ? orphans.Select(o => o.Id).ToImmutableHashSet(StringComparer.Ordinal)
                    : ImmutableHashSet<string>.Empty,
                viewerId));
    }

    /// <summary>One package's card: every line it shows, decided from the package, its install record,
    /// the viewer's admin flag and this process's activation state. Pure.</summary>
    internal static CatalogCardRow CardRow(
        LayoutAreaHost host, IPackageSource? source, PackageManifest pkg, PackageManifest? installed,
        bool viewerIsGlobalAdmin, ModuleActivationReport activation)
    {
        var card = new CatalogCardRow
        {
            Id = pkg.Id,
            Name = pkg.Name ?? pkg.Id,
            Description = pkg.Description ?? "",
            DescriptionStyle = Show(!string.IsNullOrWhiteSpace(pkg.Description), "display: block; margin-bottom: 6px;"),
            Meta = $"v{pkg.Version}  ·  {pkg.Kind}  ·  → {pkg.TargetPartition}",
        };

        // ModuleVersion (the module's OWN content hash from manifest.lock) beats the whole-repo
        // commit sha: an unrelated commit no longer flips every card to "Update". The commit-sha
        // compare stays the fallback for manifest-less packages.
        var upToDate = installed is not null
            && (!string.IsNullOrEmpty(pkg.ModuleVersion) && !string.IsNullOrEmpty(installed.ModuleVersion)
                ? string.Equals(installed.ModuleVersion, pkg.ModuleVersion, StringComparison.Ordinal)
                : string.Equals(installed.Version, pkg.Version, StringComparison.Ordinal));

        if (pkg.Refusal is { } tier)
        {
            // 🚨 #4097 — the registry declares this package in the instance's default set and
            // REFUSES it by plan tier. No button either way: the instance cannot install or update it
            // on this plan. Two truths, two sentences: with NO install record the package is not
            // here; with one — a plan DOWNGRADE after an install — it IS here and keeps working, and
            // the plan no longer covers it, so updates stop. Platform-owned chrome follows the VIEWER.
            return card with
            {
                Status = installed is null
                    ? "⛔ " + host.Localize("ui.packageRefusedByPlanTier",
                        pkg.Name ?? pkg.Id, tier.RequiredTier, tier.InstancePlan)
                    : "⚠️ " + host.Localize("ui.packageInstalledAboveThisPlan",
                        installed.Version ?? "?", pkg.Name ?? pkg.Id, tier.RequiredTier, tier.InstancePlan),
                StatusStyle = (installed is null
                                  ? "color: var(--error-foreground, #a4262c); "
                                  : "color: var(--warning-foreground, #9d5d00); ")
                              + "font-size: 12px; display: block; margin-top: 6px;",
            };
        }

        if (upToDate && installed is not null)
            card = card with
            {
                Status = host.Localize("ui.catalogInstalledVersion", installed.Version),
                StatusStyle = "color: var(--success-foreground, #107c10); font-weight: 600;",
            };
        else if (pkg.IsCommercial() && !viewerIsGlobalAdmin)
            // A commercial package needs Global Admin to install or sync (#830). The real enforcement
            // is on the ACTION (PackageEntitlement, inside the installer); this is only so a viewer is
            // not offered a button whose click would be refused.
            card = card with
            {
                Status = host.Localize("ui.requiresGlobalAdmin"),
                StatusStyle = "color: var(--neutral-foreground-hint); font-size: 12px; display: block;",
            };
        else if (source is not null)
            card = card with
            {
                InstallLabel = installed is null
                    ? host.Localize("ui.catalogInstall")
                    : host.Localize("ui.catalogUpdateTo", pkg.Version),
                InstallStyle = "",
            };

        // The per-PACKAGE update policy (Auto / Notify / Pinned) — a global administrator's control,
        // on an INSTALLED package only. Separate from the platform's own update policy on the
        // Settings ▸ Update policy tab: that one moves the image, this one moves this package.
        if (installed is not null && viewerIsGlobalAdmin)
        {
            var policy = installed.EffectiveUpdatePolicy;
            string Accent(PackageUpdatePolicy p) => p == policy ? Appearance.Accent : Appearance.Neutral;
            card = card with
            {
                PolicyStyle = "",
                AutoAppearance = Accent(PackageUpdatePolicy.Auto),
                NotifyAppearance = Accent(PackageUpdatePolicy.Notify),
                NoneAppearance = Accent(PackageUpdatePolicy.None),
            };
        }

        card = WithActivationNote(host, card, $"{PackageInstaller.InstalledPartition}/{pkg.Id}", activation);

        // 🚨 #3648 — what the module DECLARES, beside whatever state it is in, never instead of it.
        if (activation.FloorAdvisoryForPackage($"{PackageInstaller.InstalledPartition}/{pkg.Id}") is { } floor)
            card = card with
            {
                Floor = $"ℹ️ {host.Localize("ui.moduleDeclaresNewerPlatform", floor.DeclaredFloor, floor.RunningVersion ?? "?")}",
                FloorStyle = "color: var(--neutral-foreground-hint, #605e5c); font-size: 12px; display: block; margin-top: 6px;",
            };
        return card;
    }

    // The module's activation state on THIS process, rendered UNDER the status line rather than
    // instead of it — both facts are true. Matched on the install record's PATH, exactly what the
    // landing recorded on the activation entry. A blank/undetermined answer renders nothing (see
    // ModuleActivationReport.IsPendingForPackage for why silence is the honest fallback).
    private static CatalogCardRow WithActivationNote(
        LayoutAreaHost host, CatalogCardRow card, string recordPath, ModuleActivationReport activation)
    {
        const string warning = "color: var(--warning-foreground, #9d5d00); font-size: 12px; display: block; margin-top: 6px;";
        const string error = "color: var(--error-foreground, #a4262c); font-size: 12px; display: block; margin-top: 6px;";

        // 🚨 #1979 — the LAST STEP of the install, said out loud: restart-as-activation.
        if (activation.IsPendingForPackage(recordPath))
            return card with { Note = $"🔄 {host.Localize("ui.restartRequiredToActivate")}", NoteStyle = warning };

        // 🚨 #3538 — linked against a platform this deployment is not running: refused at load, and a
        // restart re-runs the same measurement, so "restart required" would be a promise no restart keeps.
        if (activation.IsQuarantinedForPackage(recordPath))
            return card with { Note = $"⚠️ {host.Localize("ui.moduleBuiltForNewerPlatform")}", NoteStyle = error };

        // 🚨 #4083 — a landing this installation REFUSED: the bytes cannot bind on this platform.
        if (activation.RefusalForPackage(recordPath) is { } refusal)
            return card with
            {
                Note = "⛔ " + (refusal.Provides is not null
                    ? host.Localize("ui.moduleHeldAtLanding", refusal.Needs ?? "?", refusal.Provides)
                    : host.Localize("ui.moduleHeldAtLandingTypes", refusal.Needs ?? "?")),
                NoteStyle = error,
            };

        // 🚨 MeshWeaver#4550 — the landed generation was DECLINED for the copy this image ships: the
        // module RUNS, from the image's copy.
        if (activation.DeclineForPackage(recordPath) is { } decline)
            return card with { Note = $"ℹ️ {host.Localize("ui.moduleRunsImageCopy", decline.Version ?? "?")}", NoteStyle = warning };

        // 🚨 #3649 — the newest generation does not load here, so this installation runs the previous one.
        if (activation.FallbackForPackage(recordPath) is { } fallback)
            return card with
            {
                Note = $"ℹ️ {host.Localize("ui.moduleRunsPreviousVersion", fallback.PreviousVersion ?? "?", fallback.Version ?? "?")}",
                NoteStyle = warning,
            };

        return card;
    }
}
