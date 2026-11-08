using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Graph.Apps;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// The home's <b>type sections</b>, below the Apps band (policy <c>home-is-composer-apps-types</c>,
/// Doc/Architecture/AppsHome → "Type sections"): ONE section per node type the viewer has content
/// of — the spaces they can reach, the things in their own home, what was shared with them — and
/// only those, so an empty section never renders and no section carries another one's copy.
/// <para><b>A type an app catalogs is not here.</b> The preferred design is that every kind of
/// content gets its own catalog app (mail → Inbox, threads → Threads, CRM clients → the CRM app);
/// such an app DECLARES the types it catalogs (<see cref="AppRoot.Catalogs"/> — the package root's
/// <c>catalogs</c> — or a module's <see cref="AppCatalogDeclaration"/>), and while it is installed those types
/// get no section. What is left — the unstructured rest — is listed by type.</para>
/// <para><b>The viewer arranges them</b>: hide, show and reorder, stored in their launcher
/// arrangement (<see cref="LauncherArrangement.Sections"/>, <c>{user}/_Settings/Launcher</c>), on the
/// <see cref="CustomizeArea"/> page, built from the platform's own controls so every client renders
/// the same editor. Default: every type with content, most recently changed first.</para>
/// </summary>
public static class HomeTypeSections
{
    /// <summary>The user-hub area that renders the type sections (embedded by the Catalog area).</summary>
    public const string AreaName = "HomeSections";

    /// <summary>The user-hub area where the owner hides, shows and reorders the sections.</summary>
    public const string CustomizeArea = "CustomizeHome";

    /// <summary>How many rows the discovery reads. Bounds the cost; a type with only older rows
    /// past it is simply not discovered until something of it changes.</summary>
    internal const int DiscoveryLimit = 500;

    /// <summary>Tiles a section shows before its "show all".</summary>
    internal const int SectionItemLimit = 48;

    private const string StoreExclusions = " -nodeType:Store/Plugin -nodeType:Store/Catalog";

    /// <summary>
    /// Types that never get a section whatever the viewer holds: the home's own plumbing (a person,
    /// an app record, the launcher arrangement) and store items, which are apps and appear once, on
    /// the Apps band. Not a list of content types — content types opt out by being catalogued.
    /// </summary>
    internal static readonly ImmutableHashSet<string> NeverSectioned = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase,
        UserNodeType.NodeType, AppNodeType.NodeType, LauncherArrangementPaths.NodeType,
        "Store/Plugin", "Store/Catalog");

    /// <summary>One type section: the node type, how many rows of it were discovered, and when
    /// the newest of them changed (the default order).</summary>
    /// <param name="NodeType">The NodeType path.</param>
    /// <param name="Count">Rows of it among the discovered ones (at least one).</param>
    /// <param name="LastModified">The newest row's change time.</param>
    public sealed record TypeSection(string NodeType, int Count, DateTimeOffset LastModified);

    /// <summary>The arranged sections: the ones shown, in the viewer's order, and the ones hidden.</summary>
    /// <param name="Shown">The sections that render, top first.</param>
    /// <param name="Hidden">The sections the viewer hid (listed on the customize page only).</param>
    /// <param name="Legs">The content legs every section query narrows.</param>
    public sealed record Arrangement(ImmutableList<TypeSection> Shown, ImmutableList<TypeSection> Hidden, ImmutableList<string> Legs)
    {
        /// <summary>The discovered sections in their DEFAULT order, before the viewer's arrangement —
        /// what a change re-arranges against, so a move is computed from the stored arrangement it
        /// patches rather than from the page the click came from.</summary>
        public ImmutableList<TypeSection> Discovered { get; init; } = [];

        /// <summary>The shown types, top first, under <paramref name="prefs"/> — the order a move
        /// shifts within. Pure.</summary>
        public ImmutableList<string> VisibleOrder(HomeSections prefs) =>
            [.. Arrange(Discovered, prefs, Legs).Shown.Select(s => s.NodeType)];

        /// <summary>Nothing to show and nothing hidden.</summary>
        public bool IsEmpty => Shown.Count == 0 && Hidden.Count == 0;
    }

    /// <summary>
    /// The content the sections are cut from, as union legs WITHOUT sort: the spaces in the viewer's
    /// profile manifest (<see cref="User.SpacePaths"/>, only Space roots), the viewer's own home
    /// children, and the scopes shared with them (<see cref="User.SharedPaths"/>, #385). Every leg is
    /// anchored — never a mesh-wide read. Pure.
    /// </summary>
    public static ImmutableList<string> ContentLegs(
        string ownerId, IReadOnlyList<string>? spacePaths, IReadOnlyList<string>? sharedTargets)
    {
        var legs = ImmutableList.CreateBuilder<string>();
        if (spacePaths is { Count: > 0 })
            legs.Add($"path:{string.Join("|", spacePaths)} is:main is:content nodeType:Space");
        legs.Add($"namespace:{ownerId} is:main is:content{StoreExclusions}");
        if (sharedTargets is { Count: > 0 })
            legs.Add($"path:{string.Join("|", sharedTargets)} is:main is:content -nodeType:User{StoreExclusions}");
        return legs.ToImmutable();
    }

    /// <summary>
    /// <see cref="ContentLegs"/> from the owner's profile manifests, SCREENED first: the paths are
    /// interpolated into query strings (which a search view exposes in its options editor and in the
    /// <c>hq=</c> URL), so a name marked for presentation mode (#1803) is dropped before any query
    /// exists. Pure.
    /// </summary>
    public static ImmutableList<string> LegsFor(string ownerId, User? user, PresentationScreen? screen)
    {
        var privacy = screen ?? PresentationScreen.Off;
        return ContentLegs(ownerId, privacy.Retain(user?.SpacePaths), privacy.Retain(user?.SharedPaths));
    }

    /// <summary>The discovery queries: each leg projected to what the grouping needs, newest first. Pure.</summary>
    public static string[] DiscoveryQueries(IReadOnlyList<string> legs) =>
        [.. legs.Select(leg => $"{leg} select:path,id,namespace,nodeType,lastModified sort:LastModified-desc limit:{DiscoveryLimit}")];

    /// <summary>
    /// The non-empty types among <paramref name="rows"/>, newest first — leaving out every type an
    /// installed app catalogs (<paramref name="cataloged"/>) and the plumbing types
    /// (<see cref="NeverSectioned"/>). A row seen by two legs counts once. Pure.
    /// </summary>
    public static ImmutableList<TypeSection> Discover(IEnumerable<MeshNode> rows, CatalogedTypeSet cataloged) =>
        [.. rows
            .Where(r => !string.IsNullOrWhiteSpace(r.NodeType) && !string.IsNullOrEmpty(r.Path))
            .DistinctBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .Where(r => !cataloged.Contains(r.NodeType) && !NeverSectioned.Contains(r.NodeType!))
            .GroupBy(r => r.NodeType!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new TypeSection(g.Key, g.Count(), g.Max(r => r.LastModified)))
            .OrderByDescending(s => s.LastModified)
            .ThenBy(s => s.NodeType, StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// <paramref name="discovered"/> as the viewer arranged it: hidden types apart, then the types
    /// the viewer ordered in their order, then the rest in the discovered (newest first) order. A
    /// preference for a type with no content now changes nothing — it waits for content. Pure.
    /// </summary>
    public static Arrangement Arrange(ImmutableList<TypeSection> discovered, HomeSections? prefs, ImmutableList<string> legs)
    {
        prefs ??= new HomeSections();
        var rank = prefs.Order
            .Select((type, index) => (type, index))
            .GroupBy(p => p.type, StringComparer.OrdinalIgnoreCase)
            .ToImmutableDictionary(g => g.Key, g => g.First().index, StringComparer.OrdinalIgnoreCase);
        var shown = discovered
            .Where(s => !prefs.IsHidden(s.NodeType))
            .Select((s, index) => (s, index))
            .OrderBy(p => rank.TryGetValue(p.s.NodeType, out var r) ? r : int.MaxValue)
            .ThenBy(p => p.index)
            .Select(p => p.s)
            .ToImmutableList();
        var hidden = discovered.Where(s => prefs.IsHidden(s.NodeType)).ToImmutableList();
        return new Arrangement(shown, hidden, legs) { Discovered = discovered };
    }

    /// <summary>
    /// One section's query: the content legs narrowed to <paramref name="nodeType"/>, newest first.
    /// The spaces leg holds only Space roots, so it joins only the Space section. Pure.
    /// </summary>
    public static string SectionQuery(IReadOnlyList<string> legs, string nodeType)
    {
        var isSpace = string.Equals(nodeType, "Space", StringComparison.OrdinalIgnoreCase);
        return string.Join("\n", legs
            .Where(leg => isSpace || !leg.EndsWith(" nodeType:Space", StringComparison.Ordinal))
            .Select(leg => leg.EndsWith(" nodeType:Space", StringComparison.Ordinal)
                ? $"{leg} sort:LastModified-desc"
                : $"{leg} nodeType:{nodeType} sort:LastModified-desc"));
    }

    /// <summary>The section title: the type's plural label (catalog key <c>nav.section.{type}</c>,
    /// else the type's name made plural — the index's own rule). Pure.</summary>
    public static string Label(string nodeType, string? locale) => IndexSections.Label(nodeType, locale);

    /// <summary>
    /// One section: a titled icon grid of the type's nodes, newest first, painted from the query
    /// rows. No search box, no filter, no view switch and no empty copy — a section exists because
    /// the type has content, and a renderer hides a titled band whose query answers empty. Pure.
    /// </summary>
    public static MeshSearchControl Section(TypeSection section, IReadOnlyList<string> legs, string? locale) =>
        Controls.MeshSearch
            .WithTitle(Label(section.NodeType, locale))
            .WithHiddenQuery(SectionQuery(legs, section.NodeType))
            .WithShowSearchBox(false)
            .WithShowEmptyMessage(false)
            .WithRenderMode(MeshSearchRenderMode.Icons)
            .WithCollapsibleSections(false)
            .WithSectionCounts(false)
            .WithItemLimit(SectionItemLimit)
            .WithMaxRows(2)
            .WithReactiveMode(true);

    /// <summary>
    /// The sections region: one <see cref="Section"/> per shown type, then — for the owner, once
    /// there is anything to arrange — a quiet "Customize sections" link to <see cref="CustomizeArea"/>.
    /// Nothing at all when the viewer has no content of a non-catalogued type. Pure.
    /// </summary>
    public static UiControl Build(Arrangement arrangement, string ownerId, string? locale, bool canCustomize)
    {
        var stack = Controls.Stack.WithWidth("100%").WithVerticalGap(24);
        foreach (var section in arrangement.Shown)
            stack = stack.WithView(Section(section, arrangement.Legs, locale), "Section-" + SectionId(section.NodeType));
        if (canCustomize && !arrangement.IsEmpty)
            stack = stack.WithView(
                Link(LocalizationCatalog.Get("home.customizeSections", locale), CustomizeHref(ownerId)),
                "Customize");
        return stack;
    }

    /// <summary>
    /// The customize page: every discovered type, shown ones first in their order, each with
    /// Hide / Move up / Move down; then the hidden ones with Show. Built from the platform's layout
    /// controls only (stack, label, button, nav link), so web, Blazor and the native app render the
    /// same editor from the same config. <paramref name="change"/> writes one change to the
    /// viewer's arrangement and completes when the write is confirmed. Pure apart from the click handlers it wires.
    /// </summary>
    public static UiControl BuildCustomize(
        Arrangement arrangement, string ownerId, string? locale,
        Func<Func<HomeSections, HomeSections>, IObservable<System.Reactive.Unit>> change)
    {
        // A move is computed INSIDE the change, from the stored arrangement the write patches — never
        // from the order this page was rendered with, so two clicks that land before the re-render
        // compose instead of the second one undoing the first.
        var page = Controls.Stack.WithWidth("100%").WithVerticalGap(16)
            .WithView(Controls.Title(LocalizationCatalog.Get("home.customizeTitle", locale), 2), "Title")
            .WithView(Controls.Markdown(LocalizationCatalog.Get("home.customizeHint", locale)), "Hint");
        if (arrangement.IsEmpty)
            page = page.WithView(Controls.Markdown(LocalizationCatalog.Get("home.customizeNothing", locale)), "Nothing");
        var shown = Controls.Stack.WithWidth("100%").WithVerticalGap(8);
        for (var i = 0; i < arrangement.Shown.Count; i++)
        {
            var type = arrangement.Shown[i].NodeType;
            var row = SectionRow(arrangement.Shown[i], locale)
                .WithView(Controls.Button(LocalizationCatalog.Get("home.sectionHide", locale))
                    .WithReactiveClickAction(_ => change(p => p.SetHidden(type, true))), "Hide");
            if (i > 0)
                row = row.WithView(Controls.Button("↑")
                    .WithLabel(LocalizationCatalog.Get("home.sectionUp", locale))
                    .WithReactiveClickAction(_ => change(MoveChange(arrangement, type, -1))), "Up");
            if (i < arrangement.Shown.Count - 1)
                row = row.WithView(Controls.Button("↓")
                    .WithLabel(LocalizationCatalog.Get("home.sectionDown", locale))
                    .WithReactiveClickAction(_ => change(MoveChange(arrangement, type, 1))), "Down");
            shown = shown.WithView(row, "Shown-" + SectionId(type));
        }
        if (arrangement.Shown.Count > 0)
            page = page.WithView(shown, "Shown");
        if (arrangement.Hidden.Count > 0)
        {
            var hidden = Controls.Stack.WithWidth("100%").WithVerticalGap(8)
                .WithView(Controls.Title(LocalizationCatalog.Get("home.sectionsHidden", locale), 3), "HiddenTitle");
            foreach (var section in arrangement.Hidden)
            {
                var type = section.NodeType;
                hidden = hidden.WithView(SectionRow(section, locale)
                    .WithView(Controls.Button(LocalizationCatalog.Get("home.sectionShow", locale))
                        .WithReactiveClickAction(_ => change(p => p.SetHidden(type, false))), "Show"),
                    "Hidden-" + SectionId(type));
            }
            page = page.WithView(hidden, "Hidden");
        }
        return page.WithView(Link(LocalizationCatalog.Get("home.backToHome", locale), $"/{ownerId}"), "Back");
    }

    /// <summary>
    /// The change one Move up / Move down click writes: <paramref name="nodeType"/> shifted by
    /// <paramref name="delta"/> within the order the STORED arrangement produces when the change is
    /// applied — never the order the page was rendered with. Pure.
    /// </summary>
    internal static Func<HomeSections, HomeSections> MoveChange(Arrangement arrangement, string nodeType, int delta) =>
        prefs => prefs.Move(nodeType, delta, arrangement.VisibleOrder(prefs));

    private static StackControl SectionRow(TypeSection section, string? locale) =>
        Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap(8)
            .WithStyle("align-items: center;")
            .WithView(Controls.Label($"{Label(section.NodeType, locale)} ({section.Count})").WithStyle("flex: 1;"), "Label");

    /// <summary>Where the owner customizes the sections: <c>/{owner}/CustomizeHome</c>. Pure.</summary>
    public static string CustomizeHref(string ownerId) => $"/{ownerId}/{CustomizeArea}";

    // A plain markdown link — the one link every client renders inside a page (a NavLink is a menu item).
    private static MarkdownControl Link(string label, string href) =>
        Controls.Markdown($"[{UserActivityLayoutAreas.EscapeMarkdownText(label)}]({href})");

    /// <summary>A control id for a NodeType path (the slash is not an id character). Pure.</summary>
    internal static string SectionId(string nodeType) => nodeType.Replace('/', '_');

    // ── Reactive shells ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The sections as they stand for the home at <paramref name="host"/> — live: the profile
    /// manifests (screened for presentation mode), the instance's catalogued types, the discovered
    /// rows and the viewer's arrangement, recombined on any change.
    /// </summary>
    internal static IObservable<Arrangement> Observe(LayoutAreaHost host, string ownerId)
    {
        var options = host.Hub.JsonSerializerOptions;
        var services = host.Hub.ServiceProvider;
        var directory = services.GetService<AppDirectory>();
        var arrangements = services.GetService<LauncherArrangementSource>();
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger(typeof(HomeTypeSections));
        var cataloged = (directory?.CatalogedTypes() ?? Observable.Return(CatalogedTypeSet.Empty))
            .Catch((Exception exception) =>
            {
                logger?.LogError(exception, "[Home] the catalogued types could not be read; every type gets a section");
                return Observable.Return(CatalogedTypeSet.Empty);
            });
        var prefs = (arrangements?.Observe(ownerId) ?? Observable.Return<LauncherArrangement?>(null))
            .Select(a => a?.Sections ?? new HomeSections())
            .StartWith(new HomeSections());
        var legs = host.Workspace.GetStream(new MeshNodeReference())!
            .Select(change => change.Value.ContentAs<User>(options))
            .CombineLatest(host.ViewerScreen(), (user, screen) => LegsFor(ownerId, user, screen))
            .DistinctUntilChanged(l => string.Join("\n", l));
        var discovered = legs
            .Select(l => host.Workspace.GetQuery($"home-type-sections:{ownerId}", DiscoveryQueries(l))
                .CombineLatest(cataloged, (rows, types) => (Legs: l, Sections: Discover(rows, types))))
            .Switch();
        return discovered
            .CombineLatest(prefs, (d, p) => Arrange(d.Sections, p, d.Legs))
            .DistinctUntilChanged(a => Fingerprint(a));
    }

    private static string Fingerprint(Arrangement a) =>
        string.Join("|", a.Shown.Select(s => $"{s.NodeType}:{s.Count}")) + "#"
        + string.Join("|", a.Hidden.Select(s => $"{s.NodeType}:{s.Count}")) + "#" + string.Join("\n", a.Legs);

    /// <summary>The <see cref="AreaName"/> view: the owner's sections; nothing for anyone else.</summary>
    internal static IObservable<UiControl?> SectionsView(LayoutAreaHost host, RenderingContext _)
    {
        var ownerId = OwnerIdOf(host.Hub.Address.ToString());
        if (!UserActivityLayoutAreas.IsViewerOwner(host.ViewerContext, ownerId))
            return Observable.Return<UiControl?>(Controls.Stack);
        var locale = host.ViewerLocale();
        return Observe(host, ownerId).Select(a => (UiControl?)Build(a, ownerId, locale, canCustomize: true));
    }

    /// <summary>The <see cref="CustomizeArea"/> view: the owner's section editor; access denied otherwise.</summary>
    internal static IObservable<UiControl?> CustomizeView(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var ownerId = OwnerIdOf(hubPath);
        if (!UserActivityLayoutAreas.IsViewerOwner(host.ViewerContext, ownerId))
            return Observable.Return<UiControl?>(MeshNodeLayoutAreas.BuildAccessDenied(hubPath));
        var locale = host.ViewerLocale();
        var arrangements = host.Hub.ServiceProvider.GetRequiredService<LauncherArrangementSource>();
        var logger = host.Hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(HomeTypeSections));
        // The click is acknowledged when the write is confirmed and refused with its error, so the
        // button stays pending exactly as long as the write.
        IObservable<System.Reactive.Unit> Change(Func<HomeSections, HomeSections> change) =>
            arrangements.UpdateSections(ownerId, change)
                .Do(_ => { }, exception => logger?.LogWarning(exception, "[Home] rearranging the home sections of {Owner} failed", ownerId))
                .Select(_ => System.Reactive.Unit.Default);
        return Observe(host, ownerId).Select(a => (UiControl?)BuildCustomize(a, ownerId, locale, Change));
    }

    private static string OwnerIdOf(string nodePath) => nodePath.StartsWith("User/") ? nodePath[5..] : nodePath;
}
