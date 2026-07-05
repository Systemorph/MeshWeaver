using System.Collections.Immutable;
using System.ComponentModel;
using System.Reactive.Linq;
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
/// not on each slide).
/// <list type="bullet">
///   <item><b>Overview</b> (default): a <see cref="SplitterControl"/> — a <b>hidable</b>
///     (collapsible) left <see cref="NavMenuControl"/> listing the deck's slides in
///     manifest order, and a right welcome stage with the deck intro and a "Present" entry
///     point that opens the first slide chrome-free.</item>
///   <item><b>Present</b>: redirects straight to the first manifest slide's Present view,
///     starting the click-to-advance walk.</item>
/// </list>
/// The slides themselves stay pure content (see <see cref="SlideLayoutAreas"/>); the deck
/// alone owns the order, so re-sequencing is one edit to the manifest.
/// </summary>
public static class DeckLayoutAreas
{
    /// <summary>Area name for the chrome-free Present redirect area.</summary>
    public const string PresentArea = "Present";

    private const string DefaultIntro =
        "Use the side navigation to jump to any slide, or press **Present** to start the walk-through. "
        + "Slides advance on click.";

    private const string EmptyDeckHint =
        "*This deck has no slides yet. Create Slide children and list their ids, in order, "
        + "in the deck's `Slides` manifest.*";

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
    /// Renders the default Overview: a splitter with a collapsible left NavMenu built from
    /// the deck's ordered <see cref="DeckContent.Slides"/> manifest (resolving each child
    /// for its label), and a right welcome stage. The splitter renders once; only its panes
    /// re-render reactively, so the user's collapse state survives content updates.
    /// </summary>
    /// <param name="host">The layout area host rendering the area.</param>
    /// <param name="_">The rendering context for the area.</param>
    /// <returns>The splitter shell for the Overview layout area.</returns>
    [Browsable(false)]
    public static UiControl Overview(LayoutAreaHost host, RenderingContext _)
    {
        var deckPath = host.Hub.Address.ToString();
        var deckNodeStream = host.Workspace.GetMeshNodeStream();
        var childrenStream = ObserveDeckChildren(host, deckPath);

        return Controls.Splitter
            .WithClass("shell-splitter")
            .WithSkin(s => s.WithOrientation(Orientation.Horizontal).WithWidth("100%").WithHeight("100%"))
            .WithView(
                // Left pane: hidable NavMenu from the ordered manifest.
                (h, c) => deckNodeStream.CombineLatest(childrenStream,
                    (node, children) => BuildDeckNav(host, deckPath, node, children)),
                skin => skin.WithSize("300px").WithMin("220px").WithMax("440px").WithCollapsible(true))
            .WithView(
                // Right pane: the welcome stage with the intro + Present entry point.
                (h, c) => deckNodeStream.CombineLatest(childrenStream,
                    (node, children) => BuildDeckStage(host, deckPath, node, children)),
                skin => skin.WithSize("*"));
    }

    /// <summary>
    /// Builds the left NavMenu: one entry per manifest slide, in order, resolving each
    /// child node for its display label. The manifest is the source of truth for order.
    /// </summary>
    private static UiControl BuildDeckNav(
        LayoutAreaHost host, string deckPath, MeshNode? deckNode,
        IReadOnlyDictionary<string, MeshNode> children)
    {
        var deck = deckNode.ContentAs<DeckContent>(host.Hub.JsonSerializerOptions);
        var refs = (deck?.Slides ?? ImmutableList<string>.Empty)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .ToImmutableList();

        var navMenu = Controls.NavMenu.WithSkin(s => s.WithWidth(300).WithCollapsible(false));
        var group = new NavGroupControl(deckNode?.Name ?? deckNode?.Id ?? "Slides")
            .WithSkin(s => s.WithExpanded(true));

        if (refs.Count > 0)
        {
            var n = 0;
            foreach (var slideRef in refs)
            {
                n++;
                var path = ResolveChildPath(deckPath, slideRef);
                children.TryGetValue(path, out var child);
                var label = child?.Name ?? child?.Id ?? LastSegment(slideRef);
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
    /// manifest has at least one slide — a "Present" button opening the first slide's
    /// chrome-free Present view.
    /// </summary>
    private static UiControl BuildDeckStage(
        LayoutAreaHost host, string deckPath, MeshNode? deckNode,
        IReadOnlyDictionary<string, MeshNode> children)
    {
        var deck = deckNode.ContentAs<DeckContent>(host.Hub.JsonSerializerOptions);
        var title = string.IsNullOrWhiteSpace(deck?.Title)
            ? deckNode?.Name ?? deckNode?.Id ?? "Deck"
            : deck!.Title!;

        var firstRef = (deck?.Slides ?? ImmutableList<string>.Empty)
            .FirstOrDefault(r => !string.IsNullOrWhiteSpace(r));

        var container = Controls.Stack
            .WithWidth("100%")
            .WithStyle(MeshNodeLayoutAreas.GetContainerStyle(host));

        container = container.WithView(Controls.H1(title).WithStyle("margin: 0 0 12px 0;"));

        var intro = firstRef is null
            ? EmptyDeckHint
            : string.IsNullOrWhiteSpace(deck?.Description) ? DefaultIntro : deck!.Description!;
        container = container.WithView(Controls.Markdown(intro)
            .WithStyle("width: 100%; margin-bottom: 20px;"));

        if (firstRef is not null)
        {
            var firstPath = ResolveChildPath(deckPath, firstRef);
            container = container.WithView(Controls.Button("▶ Present")
                .WithAppearance(Appearance.Accent)
                .WithNavigateToHref(MeshNodeLayoutAreas.BuildUrl(firstPath, SlideLayoutAreas.PresentArea)));
        }

        return container;
    }

    // ── Present ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Renders the Present area: a redirect to the first manifest slide's Present view,
    /// starting the click-to-advance walk. Reactive so it settles once the deck node loads.
    /// </summary>
    /// <param name="host">The layout area host rendering the area.</param>
    /// <param name="_">The rendering context for the area.</param>
    /// <returns>An observable stream of the view for the Present layout area.</returns>
    [Browsable(false)]
    public static IObservable<UiControl?> Present(LayoutAreaHost host, RenderingContext _)
    {
        var deckPath = host.Hub.Address.ToString();
        return host.Workspace.GetMeshNodeStream()
            .Select(node =>
            {
                var deck = node.ContentAs<DeckContent>(host.Hub.JsonSerializerOptions);
                var firstRef = (deck?.Slides ?? ImmutableList<string>.Empty)
                    .FirstOrDefault(r => !string.IsNullOrWhiteSpace(r));
                if (firstRef is null)
                    return (UiControl?)Controls.Markdown(EmptyDeckHint);
                var firstPath = ResolveChildPath(deckPath, firstRef);
                return new RedirectControl(
                    MeshNodeLayoutAreas.BuildUrl(firstPath, SlideLayoutAreas.PresentArea));
            });
    }

    // ── Deck resolution helpers (shared with SlideLayoutAreas) ────────────────

    /// <summary>
    /// Resolves a manifest reference to a full child path under the deck. A reference is
    /// either the child's id (relative — <c>"intro"</c> → <c>"{deck}/intro"</c>) or already
    /// a full path under the deck, in which case it is returned unchanged.
    /// </summary>
    internal static string ResolveChildPath(string deckPath, string slideRef)
    {
        var trimmed = slideRef.Trim().TrimStart('/');
        if (trimmed.Length == 0)
            return deckPath;
        if (trimmed.StartsWith(deckPath + "/", StringComparison.Ordinal))
            return trimmed;
        return $"{deckPath}/{trimmed}";
    }

    private static string LastSegment(string reference)
    {
        var trimmed = reference.Trim().TrimEnd('/');
        var idx = trimmed.LastIndexOf('/');
        return idx >= 0 && idx < trimmed.Length - 1 ? trimmed[(idx + 1)..] : trimmed;
    }

    /// <summary>
    /// Live observable of the deck's direct children (any node type), as a path → node map,
    /// used to resolve manifest references to display labels. Starts empty so the shell
    /// renders before the first query emission.
    /// </summary>
    private static IObservable<IReadOnlyDictionary<string, MeshNode>> ObserveDeckChildren(
        LayoutAreaHost host, string deckPath)
    {
        var meshService = host.Hub.ServiceProvider.GetService<IMeshService>();
        if (meshService is null)
            return Observable.Return<IReadOnlyDictionary<string, MeshNode>>(
                ImmutableDictionary<string, MeshNode>.Empty);

        return meshService
            .Query<MeshNode>(MeshQueryRequest.FromQuery($"namespace:{deckPath}"))
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
            .Select(map => (IReadOnlyDictionary<string, MeshNode>)map)
            .StartWith((IReadOnlyDictionary<string, MeshNode>)ImmutableDictionary<string, MeshNode>.Empty);
    }
}
