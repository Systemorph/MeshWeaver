using System.Collections.Immutable;
using System.ComponentModel;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Layout views for Deck nodes — a presentation (or a course sequence) whose slide/page
/// order is declared EXTERNALLY in <see cref="DeckContent.Slides"/> (on the deck node,
/// not on each slide). Presentation is fully DECOUPLED from the slide nodes: a referenced
/// slide may live anywhere in the mesh, and the SAME slide path may appear in many decks in
/// different orders — each deck presents it in its own order.
/// <list type="bullet">
///   <item><b>Overview</b> (default): a <see cref="SplitterControl"/> — a <b>hidable</b>
///     (collapsible) left <see cref="NavMenuControl"/> listing the deck's referenced slides in
///     manifest order (each resolved by path for its label), and a right welcome stage with the
///     deck intro and a "Present" entry point that opens the walk chrome-free.</item>
///   <item><b>Present</b>: the deck-DRIVEN full-screen walk. <c>{deck}/Present</c> shows the
///     first referenced slide's stage full-screen; <c>?i=N</c> selects the slide, and prev /
///     next / index / count all come from the DECK's manifest — so a slide shared by two decks
///     presents correctly in each deck's order. Keyboard + click both advance.</item>
/// </list>
/// The slides themselves stay pure content (see <see cref="SlideLayoutAreas"/>); the deck alone
/// owns the order, so re-sequencing is one edit to the manifest.
/// </summary>
public static class DeckLayoutAreas
{
    /// <summary>Area name for the deck-driven full-screen Present walk.</summary>
    public const string PresentArea = "Present";

    private const string DefaultIntro =
        "Use the side navigation to jump to any slide, or press **Present** to start the walk-through. "
        + "Slides advance on click, or with the arrow / space / page keys.";

    private const string EmptyDeckHint =
        "*This deck has no slides yet. Add slide references (ids or paths), in order, "
        + "to the deck's `Slides` manifest.*";

    /// <summary>
    /// Registers the Deck node views (Overview, Present and the standard create/delete
    /// areas) on the hub configuration.
    /// </summary>
    /// <param name="configuration">The message hub configuration to register on.</param>
    /// <returns>The configuration with the Deck views registered.</returns>
    public static MessageHubConfiguration AddDeckViews(this MessageHubConfiguration configuration)
        => configuration
            .AddDefaultLayoutAreas()
            .Set(new PageLayoutOptions { MaxWidth = "1400px" })
            .AddLayout(layout => layout
                // Overriding the default Overview with the deck's splitter shell.
                .WithView(MeshNodeLayoutAreas.OverviewArea, Overview)
                .WithView(PresentArea, Present)
                .WithView(MeshNodeLayoutAreas.CreateNodeArea, CreateLayoutArea.Create)
                .WithView(MeshNodeLayoutAreas.DeleteArea, DeleteLayoutArea.Delete));

    // ── Overview ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Renders the default Overview: a splitter with a collapsible left NavMenu built from the
    /// deck's ordered <see cref="DeckContent.Slides"/> manifest (each referenced slide resolved
    /// by path for its label), and a right welcome stage. The splitter renders once; only its
    /// panes re-render reactively, so the user's collapse state survives content updates.
    /// </summary>
    /// <param name="host">The layout area host rendering the area.</param>
    /// <param name="_">The rendering context for the area.</param>
    /// <returns>The splitter shell for the Overview layout area.</returns>
    [Browsable(false)]
    public static UiControl Overview(LayoutAreaHost host, RenderingContext _)
    {
        var deckPath = host.Hub.Address.ToString();
        var deckNodeStream = host.Workspace.GetMeshNodeStream();
        var slidesStream = ObserveSlideNodes(host, deckPath);

        return Controls.Splitter
            .WithClass("shell-splitter")
            .WithSkin(s => s.WithOrientation(Orientation.Horizontal).WithWidth("100%").WithHeight("100%"))
            .WithView(
                // Left pane: hidable NavMenu from the ordered manifest.
                (h, c) => deckNodeStream.CombineLatest(slidesStream,
                    (node, slides) => BuildDeckNav(deckPath, node, slides)),
                skin => skin.WithSize("300px").WithMin("220px").WithMax("440px").WithCollapsible(true))
            .WithView(
                // Right pane: the welcome stage with the intro + Present entry point.
                (h, c) => deckNodeStream.CombineLatest(slidesStream,
                    (node, slides) => BuildDeckStage(host, deckPath, node, slides)),
                skin => skin.WithSize("*"));
    }

    /// <summary>
    /// Builds the left NavMenu: one entry per manifest slide, in order, using each resolved
    /// node's display label. The manifest is the source of truth for order.
    /// </summary>
    private static UiControl BuildDeckNav(
        string deckPath, MeshNode? deckNode, IReadOnlyList<(string Path, MeshNode? Node)> slides)
    {
        var navMenu = Controls.NavMenu.WithSkin(s => s.WithWidth(300).WithCollapsible(false));
        var group = new NavGroupControl(deckNode?.Name ?? deckNode?.Id ?? "Slides")
            .WithSkin(s => s.WithExpanded(true));

        if (slides.Count > 0)
        {
            var n = 0;
            foreach (var (path, node) in slides)
            {
                n++;
                var label = node?.Name ?? node?.Id ?? LastSegment(path);
                group = group.WithView(new NavLinkControl($"{n}. {label}", null, $"/{path}"));
            }
        }
        else
        {
            group = group.WithView(Controls.Body("No slides in this deck yet.")
                .WithStyle("padding: 4px 16px; display: block; color: var(--neutral-foreground-hint);"));
        }

        return navMenu.WithNavGroup(group);
    }

    /// <summary>
    /// Builds the right welcome stage: the deck title, its markdown intro, and — when the
    /// manifest has at least one slide — a "Present" button opening the deck's chrome-free
    /// full-screen walk.
    /// </summary>
    private static UiControl BuildDeckStage(
        LayoutAreaHost host, string deckPath, MeshNode? deckNode,
        IReadOnlyList<(string Path, MeshNode? Node)> slides)
    {
        var deck = deckNode.ContentAs<DeckContent>(host.Hub.JsonSerializerOptions);
        var title = string.IsNullOrWhiteSpace(deck?.Title)
            ? deckNode?.Name ?? deckNode?.Id ?? "Deck"
            : deck!.Title!;

        var hasSlides = slides.Count > 0;

        var container = Controls.Stack
            .WithWidth("100%")
            .WithStyle(MeshNodeLayoutAreas.GetContainerStyle(host));

        container = container.WithView(Controls.H1(title).WithStyle("margin: 0 0 12px 0;"));

        var intro = !hasSlides
            ? EmptyDeckHint
            : string.IsNullOrWhiteSpace(deck?.Description) ? DefaultIntro : deck!.Description!;
        container = container.WithView(Controls.Markdown(intro)
            .WithStyle("width: 100%; margin-bottom: 20px;"));

        if (hasSlides)
        {
            container = container.WithView(Controls.Button("▶ Present")
                .WithAppearance(Appearance.Accent)
                .WithNavigateToHref(MeshNodeLayoutAreas.BuildUrl(deckPath, PresentArea)));
        }

        return container;
    }

    // ── Present ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Renders the deck-driven Present walk: the referenced slide at <c>?i</c> (default 0,
    /// clamped) rendered full-screen, with the deck's manifest driving prev / next / index /
    /// count. Reactive so it settles as the deck and its referenced slides load.
    /// </summary>
    /// <param name="host">The layout area host rendering the area.</param>
    /// <param name="_">The rendering context for the area.</param>
    /// <returns>An observable stream of the view for the Present layout area.</returns>
    [Browsable(false)]
    public static IObservable<UiControl?> Present(LayoutAreaHost host, RenderingContext _)
    {
        var deckPath = host.Hub.Address.ToString();
        var requested = ReadIndex(host);
        return ObserveSlideNodes(host, deckPath)
            .Select(slides => (UiControl?)BuildDeckPresent(host, deckPath, slides, requested));
    }

    /// <summary>
    /// Builds the full-screen stage for the deck's slide at <paramref name="requested"/> (clamped
    /// into range), plus the corner counter and the keyboard driver — all sequenced by the DECK's
    /// manifest so the same slide path presents in each deck's own order.
    /// </summary>
    private static UiControl BuildDeckPresent(
        LayoutAreaHost host, string deckPath,
        IReadOnlyList<(string Path, MeshNode? Node)> slides, int requested)
    {
        var count = slides.Count;
        if (count == 0)
            return SlideLayoutAreas.BuildPresentRoot()
                .WithView(Controls.Markdown(EmptyDeckHint)
                    .WithStyle("color: var(--ae-fg);"), SlideLayoutAreas.StageArea);

        var index = Math.Clamp(requested, 0, count - 1);
        var slideContent = slides[index].Node.ContentAs<SlideContent>(host.Hub.JsonSerializerOptions);
        var nextHref = index < count - 1 ? DeckPresentUrl(deckPath, index + 1) : null;

        var root = SlideLayoutAreas.BuildPresentRoot();
        root = root.WithView(
            SlideLayoutAreas.BuildStage(slideContent, nextHref, present: true), SlideLayoutAreas.StageArea);
        root = root.WithView(
            SlideLayoutAreas.BuildPresentCounter(index, count), SlideLayoutAreas.CounterArea);
        root = root.WithView(new SlideShowControl
        {
            FirstHref = DeckPresentUrl(deckPath, 0),
            LastHref = DeckPresentUrl(deckPath, count - 1),
            PreviousHref = index > 0 ? DeckPresentUrl(deckPath, index - 1) : null,
            NextHref = nextHref,
            ExitHref = $"/{deckPath}",
        }, SlideLayoutAreas.SlideShowArea);

        return root;
    }

    /// <summary>Reads the <c>?i</c> slide index off the layout-area query (default 0, negative clamped to 0).</summary>
    private static int ReadIndex(LayoutAreaHost host)
        => int.TryParse(host.GetQueryStringParamValue("i"), out var i) && i > 0 ? i : 0;

    /// <summary>The deck Present URL for slide index <paramref name="index"/> — <c>/{deck}/Present?i={index}</c>.</summary>
    private static string DeckPresentUrl(string deckPath, int index)
        => MeshNodeLayoutAreas.BuildUrl(deckPath, PresentArea, $"i={index}");

    // ── Slide resolution (decoupled: references may point anywhere) ────────────

    /// <summary>
    /// Resolves a manifest reference to a full slide path. A reference is either a bare id
    /// (relative to the deck — <c>"intro"</c> → <c>"{deck}/intro"</c>, kept for backward
    /// compatibility) or an ABSOLUTE path to a slide anywhere in the mesh (any reference
    /// containing a <c>/</c> is treated as an absolute path and returned as-is).
    /// </summary>
    internal static string ResolveSlidePath(string deckPath, string slideRef)
    {
        var trimmed = slideRef.Trim().TrimStart('/');
        if (trimmed.Length == 0)
            return deckPath;
        // A reference that names a path (contains '/') points at a slide ANYWHERE — presentation
        // is decoupled from the deck's children. A bare id is a child under the deck.
        return trimmed.Contains('/') ? trimmed : $"{deckPath}/{trimmed}";
    }

    private static string LastSegment(string reference)
    {
        var trimmed = reference.Trim().TrimEnd('/');
        var idx = trimmed.LastIndexOf('/');
        return idx >= 0 && idx < trimmed.Length - 1 ? trimmed[(idx + 1)..] : trimmed;
    }

    /// <summary>
    /// Live observable of the deck's referenced slides, in manifest order, each resolved by PATH
    /// via <c>workspace.GetMeshNodeStream(path)</c> (the slides may live ANYWHERE — presentation
    /// is decoupled from the slide nodes). Re-subscribes the per-slide streams only when the
    /// resolved path list itself changes (<c>DistinctUntilChanged</c> on the joined paths);
    /// starts empty so the shell renders before the first emission.
    /// </summary>
    internal static IObservable<IReadOnlyList<(string Path, MeshNode? Node)>> ObserveSlideNodes(
        LayoutAreaHost host, string deckPath)
        => host.Workspace.GetMeshNodeStream()
            .Select(node =>
            {
                var deck = node.ContentAs<DeckContent>(host.Hub.JsonSerializerOptions);
                var paths = (deck?.Slides ?? ImmutableList<string>.Empty)
                    .Where(r => !string.IsNullOrWhiteSpace(r))
                    .Select(r => ResolveSlidePath(deckPath, r))
                    .ToImmutableList();
                // Empty manifest → a live query: the deck's custom Query, else the DEFAULT subtree of
                // Slide nodes (a deck can be just "a folder of slides"; only Slide nodes count).
                var query = paths.Count > 0
                    ? null
                    : string.IsNullOrWhiteSpace(deck?.Query)
                        ? $"path:{deckPath} nodeType:{SlideNodeType.NodeType} scope:subtree"
                        : deck!.Query!.Trim();
                return (Paths: paths, Query: query);
            })
            // Re-resolve only when the selection actually changes (the ordered manifest, or the query).
            .DistinctUntilChanged(x => string.Join("\n", x.Paths) + "" + (x.Query ?? ""))
            .Select(x => x.Paths.Count > 0
                // Explicit manifest: exactly these paths, in the order declared — never re-sorted.
                ? Observable.CombineLatest(x.Paths.Select(ObserveSlide(host)))
                    .Select(items => (IReadOnlyList<(string, MeshNode?)>)items.ToImmutableList())
                // Query/subtree: the live match, ordered by MeshNode.Order (nulls last, ties by path).
                : ObserveQuerySlides(host, x.Query!, deckPath))
            .Switch()
            .StartWith((IReadOnlyList<(string, MeshNode?)>)ImmutableList<(string, MeshNode?)>.Empty);

    /// <summary>
    /// A live (synced) query for a deck's slides when it has no explicit manifest: runs
    /// <paramref name="query"/> (a custom <see cref="DeckContent.Query"/> or the default
    /// <c>path:{deck} nodeType:Slide scope:subtree</c>), accumulates the reactive change stream, drops the deck
    /// root itself and any <c>_</c>-prefixed governance node, and orders the result by
    /// <see cref="MeshNode.Order"/> (nulls last, then by path). So a deck whose slides are its
    /// children needs no manifest, and re-ordering is just editing each slide's <c>Order</c>.
    /// </summary>
    private static IObservable<IReadOnlyList<(string Path, MeshNode? Node)>> ObserveQuerySlides(
        LayoutAreaHost host, string query, string deckPath)
    {
        var meshService = host.Hub.ServiceProvider.GetService<IMeshService>();
        if (meshService is null)
            return Observable.Return((IReadOnlyList<(string, MeshNode?)>)ImmutableList<(string, MeshNode?)>.Empty);

        return meshService
            .Query<MeshNode>(MeshQueryRequest.FromQuery(query))
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
            .Select(map => (IReadOnlyList<(string, MeshNode?)>)map.Values
                .Where(n => !string.Equals(n.Path, deckPath, StringComparison.Ordinal)
                            && !n.Segments.Skip(1).Any(s => s.StartsWith('_')))
                .OrderBy(n => n.Order ?? int.MaxValue)
                .ThenBy(n => n.Path, StringComparer.Ordinal)
                .Select(n => (n.Path, (MeshNode?)n))
                .ToImmutableList())
            .StartWith((IReadOnlyList<(string, MeshNode?)>)ImmutableList<(string, MeshNode?)>.Empty);
    }

    /// <summary>One resolved slide stream: its path paired with the live node (null until it loads / if missing).</summary>
    private static Func<string, IObservable<(string Path, MeshNode? Node)>> ObserveSlide(LayoutAreaHost host)
        => path => host.Workspace.GetMeshNodeStream(path)
            .Select(node => (Path: path, Node: (MeshNode?)node))
            .StartWith((path, (MeshNode?)null));
}
