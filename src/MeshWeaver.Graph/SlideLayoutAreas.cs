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
/// Layout views for Slide nodes — presentation-style pages for training material.
/// <list type="bullet">
///   <item><b>Content</b> (default): the slide rendered on a 16:9 stage with a slim
///     presenter bar underneath (Prev / "Slide n / N" / Deck / Present / Next).
///     Clicking the stage advances to the next slide.</item>
///   <item><b>Present</b>: the chrome-free stage — click to advance, a small corner
///     counter is the only overlay. Open this full-screen to present.</item>
///   <item><b>Notes</b>: speaker notes plus a compact preview of the slide.</item>
/// </list>
/// A deck is any parent whose children are Slide nodes; prev/next resolve the sibling
/// Slide nodes of the same parent ordered by <see cref="MeshNode.Order"/> (lower first,
/// null last, ties broken by path). Navigation uses the standard href/redirect
/// mechanism (<c>WithNavigateToHref</c> for buttons, <see cref="RedirectControl"/>
/// from the stage click action) — no bespoke messages.
/// </summary>
public static class SlideLayoutAreas
{
    /// <summary>Area name for the Content layout area (default).</summary>
    public const string ContentArea = "Content";
    /// <summary>Area name for the chrome-free Present layout area.</summary>
    public const string PresentArea = "Present";
    /// <summary>Area name for the speaker Notes layout area.</summary>
    public const string NotesArea = "Notes";

    /// <summary>Area id of the slide stage inside the Content / Present / Notes areas.</summary>
    public const string StageArea = "Stage";
    /// <summary>Area id of the presenter bar beneath the stage in the Content area.</summary>
    public const string PresenterBarArea = "PresenterBar";
    /// <summary>Area id of the "Slide n / N" counter.</summary>
    public const string CounterArea = "Counter";
    /// <summary>Area id of the Prev button inside the presenter bar.</summary>
    public const string PrevButtonArea = "Prev";
    /// <summary>Area id of the Next button inside the presenter bar.</summary>
    public const string NextButtonArea = "Next";
    /// <summary>Area id of the deck (parent) link inside the presenter bar.</summary>
    public const string DeckLinkArea = "Deck";
    /// <summary>Area id of the "Present" link inside the presenter bar.</summary>
    public const string PresentLinkArea = "PresentLink";
    /// <summary>Area id of the notes body inside the Notes area.</summary>
    public const string NotesBodyArea = "NotesBody";

    /// <summary>
    /// Theme-aware default stage background — a subtle gradient built from the
    /// design-token layer colors so it adapts to light and dark themes.
    /// <see cref="SlideContent.Background"/> overrides it per slide.
    /// </summary>
    private const string DefaultBackground =
        "linear-gradient(150deg, var(--neutral-layer-2) 0%, var(--neutral-layer-1) 55%, var(--neutral-layer-2) 100%)";

    private const string EmptySlideHint =
        "*This slide has no content yet. Edit the node's `Content` to fill the stage.*";

    /// <summary>
    /// Registers the Slide node views (Content, Present, Notes and the standard
    /// create/delete areas) on the hub configuration.
    /// </summary>
    /// <param name="configuration">The message hub configuration to register on.</param>
    /// <returns>The configuration with the Slide views registered.</returns>
    public static MessageHubConfiguration AddSlideViews(this MessageHubConfiguration configuration)
        => configuration
            .AddDefaultLayoutAreas()
            // Slides want a wide reading column — the stage is the page.
            .Set(new PageLayoutOptions { MaxWidth = "1400px" })
            .AddLayout(layout => layout
                .WithDefaultArea(ContentArea)
                .WithView(ContentArea, Content)
                .WithView(PresentArea, Present)
                .WithView(NotesArea, Notes)
                .WithView(MeshNodeLayoutAreas.CreateNodeArea, CreateLayoutArea.Create)
                .WithView(MeshNodeLayoutAreas.DeleteArea, DeleteLayoutArea.Delete));

    /// <summary>
    /// Renders the default Content area: the slide stage (click advances to the next
    /// slide) with a slim presenter bar underneath — Prev / "Slide n / N" / Deck /
    /// Present / Next. Prev/Next resolve sibling Slide nodes of the same parent.
    /// </summary>
    /// <param name="host">The layout area host rendering the area.</param>
    /// <param name="_">The rendering context for the area.</param>
    /// <returns>An observable stream of the view for the Content layout area.</returns>
    [Browsable(false)]
    public static IObservable<UiControl?> Content(LayoutAreaHost host, RenderingContext _)
        => host.Workspace.GetMeshNodeStream()
            .CombineLatest(ObserveDeckSlides(host),
                (node, slides) => (UiControl?)BuildContent(host, node, slides));

    /// <summary>
    /// Renders the Present area: just the stage, click-to-advance (staying in Present
    /// mode), with a minimal counter overlay in the corner. This is what a presenter
    /// opens full-screen in the browser.
    /// </summary>
    /// <param name="host">The layout area host rendering the area.</param>
    /// <param name="_">The rendering context for the area.</param>
    /// <returns>An observable stream of the view for the Present layout area.</returns>
    [Browsable(false)]
    public static IObservable<UiControl?> Present(LayoutAreaHost host, RenderingContext _)
        => host.Workspace.GetMeshNodeStream()
            .CombineLatest(ObserveDeckSlides(host),
                (node, slides) => (UiControl?)BuildPresent(host, node, slides));

    /// <summary>
    /// Renders the Notes area: the speaker notes (markdown) with a compact preview of
    /// the slide below.
    /// </summary>
    /// <param name="host">The layout area host rendering the area.</param>
    /// <param name="_">The rendering context for the area.</param>
    /// <returns>An observable stream of the view for the Notes layout area.</returns>
    [Browsable(false)]
    public static IObservable<UiControl?> Notes(LayoutAreaHost host, RenderingContext _)
        => host.Workspace.GetMeshNodeStream()
            .Select(node => (UiControl?)BuildNotes(host, node));

    // ── Content ──────────────────────────────────────────────────────────────

    private static UiControl BuildContent(LayoutAreaHost host, MeshNode? node, IReadOnlyList<MeshNode> slides)
    {
        var hubPath = host.Hub.Address.ToString();
        var slide = node.ContentAs<SlideContent>(host.Hub.JsonSerializerOptions);
        var (index, prev, next) = Locate(hubPath, slides);
        var nextHref = next is null ? null : MeshNodeLayoutAreas.BuildUrl(next.Path, ContentArea);

        var container = Controls.Stack
            .WithWidth("100%")
            .WithStyle(MeshNodeLayoutAreas.GetContainerStyle(host));

        container = container.WithView(BuildStage(slide, nextHref), StageArea);
        container = container.WithView(
            BuildPresenterBar(hubPath, index, slides.Count, prev, next), PresenterBarArea);

        return container;
    }

    /// <summary>
    /// The slim presenter bar under the stage: ◀ Prev / "Slide n / N" / Deck /
    /// Present / Next ▶. Prev/Next render only when a sibling exists in that
    /// direction; all navigation goes through the standard href mechanism.
    /// </summary>
    private static UiControl BuildPresenterBar(
        string hubPath, int index, int count, MeshNode? prev, MeshNode? next)
    {
        var bar = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("display: flex; align-items: center; gap: 10px; margin-top: 14px;");

        if (prev is not null)
            bar = bar.WithView(Controls.Button("◀ Prev")
                    .WithAppearance(Appearance.Neutral)
                    .WithNavigateToHref(MeshNodeLayoutAreas.BuildUrl(prev.Path, ContentArea)),
                PrevButtonArea);

        bar = bar.WithView(BuildCounter(index, count)
                .WithStyle("font-variant-numeric: tabular-nums; color: var(--neutral-foreground-hint);"),
            CounterArea);

        var parentPath = GetParentPath(hubPath);
        if (parentPath is not null)
            bar = bar.WithView(Controls.Button("Deck")
                    .WithAppearance(Appearance.Lightweight)
                    .WithNavigateToHref($"/{parentPath}"),
                DeckLinkArea);

        bar = bar.WithView(Controls.Button("Present")
                .WithAppearance(Appearance.Lightweight)
                .WithNavigateToHref(MeshNodeLayoutAreas.BuildUrl(hubPath, PresentArea)),
            PresentLinkArea);

        if (next is not null)
            bar = bar.WithView(Controls.Button("Next ▶")
                    .WithAppearance(Appearance.Accent)
                    .WithStyle("margin-left: auto;")
                    .WithNavigateToHref(MeshNodeLayoutAreas.BuildUrl(next.Path, ContentArea)),
                NextButtonArea);

        return bar;
    }

    // ── Present ──────────────────────────────────────────────────────────────

    private static UiControl BuildPresent(LayoutAreaHost host, MeshNode? node, IReadOnlyList<MeshNode> slides)
    {
        var hubPath = host.Hub.Address.ToString();
        var slide = node.ContentAs<SlideContent>(host.Hub.JsonSerializerOptions);
        var (index, _, next) = Locate(hubPath, slides);
        // Advancing in Present mode stays in Present mode.
        var nextHref = next is null ? null : MeshNodeLayoutAreas.BuildUrl(next.Path, PresentArea);

        var root = Controls.Stack
            .WithWidth("100%")
            .WithStyle("position: relative; width: 100%; margin: 0 auto; max-width: 1600px; padding: 8px;");

        root = root.WithView(BuildStage(slide, nextHref), StageArea);

        // Minimal overlay counter, bottom-right corner — the only chrome in Present mode.
        root = root.WithView(BuildCounter(index, slides.Count)
                .WithStyle("position: absolute; right: 26px; bottom: 20px; " +
                           "font-size: 12px; font-variant-numeric: tabular-nums; " +
                           "opacity: 0.55; pointer-events: none;"),
            CounterArea);

        return root;
    }

    // ── Notes ────────────────────────────────────────────────────────────────

    private static UiControl BuildNotes(LayoutAreaHost host, MeshNode? node)
    {
        var slide = node.ContentAs<SlideContent>(host.Hub.JsonSerializerOptions);

        var container = Controls.Stack
            .WithWidth("100%")
            .WithStyle(MeshNodeLayoutAreas.GetContainerStyle(host));

        container = container.WithView(
            Controls.H1(node?.Name ?? node?.Id ?? "Speaker Notes").WithStyle("margin: 0 0 12px 0;"));

        container = container.WithView(
            Controls.Markdown(string.IsNullOrWhiteSpace(slide?.Notes)
                    ? "*No speaker notes for this slide.*"
                    : slide!.Notes!)
                .WithStyle("width: 100%; margin-bottom: 20px;"),
            NotesBodyArea);

        // Compact, non-interactive preview of the slide below the notes.
        container = container.WithView(
            Controls.Stack
                .WithStyle("max-width: 640px; font-size: 60%;")
                .WithView(BuildStage(slide, nextHref: null), StageArea));

        return container;
    }

    // ── Stage ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The slide stage: a 16:9 rounded, drop-shadowed surface with the slide body
    /// (markdown / HTML / SVG) vertically centered at generous padding and large
    /// fluid type. <see cref="SlideContent.Background"/> overrides the theme-aware
    /// default gradient. When <paramref name="nextHref"/> is set, clicking the stage
    /// advances by rendering a <see cref="RedirectControl"/> (standard navigation).
    /// </summary>
    private static UiControl BuildStage(SlideContent? slide, string? nextHref)
    {
        var background = string.IsNullOrWhiteSpace(slide?.Background)
            ? DefaultBackground
            : slide!.Background!;

        var stage = Controls.Stack
            .WithWidth("100%")
            .WithView(Controls.Markdown(
                    string.IsNullOrWhiteSpace(slide?.Content) ? EmptySlideHint : slide!.Content!)
                .WithStyle("width: 100%;"))
            .WithStyle(
                "aspect-ratio: 16 / 9; width: 100%; box-sizing: border-box; " +
                "display: flex; flex-direction: column; justify-content: center; " +
                "padding: 4% 7%; border-radius: 18px; overflow: hidden; " +
                "box-shadow: 0 18px 50px rgba(0, 0, 0, 0.28); " +
                "font-size: clamp(16px, 2.2vw, 34px); line-height: 1.4; " +
                $"background: {background};" +
                (nextHref is null ? "" : " cursor: pointer;"));

        if (nextHref is not null)
            stage = stage.WithClickAction(ctx =>
            {
                // Standard navigation: replace this area with a RedirectControl —
                // the client navigates to the next slide (same mechanism as
                // CodeLayoutAreas' save-then-redirect). Sync lambda, no async.
                ctx.Host.UpdateArea(ctx.Area, new RedirectControl(nextHref));
                return Task.CompletedTask;
            });

        return stage;
    }

    private static LabelControl BuildCounter(int index, int count)
        => Controls.Body($"Slide {(index >= 0 ? index + 1 : 1)} / {Math.Max(count, 1)}");

    // ── Deck resolution ──────────────────────────────────────────────────────

    /// <summary>
    /// Live observable of the deck's slides: the sibling Slide nodes sharing this
    /// node's parent, ordered by <see cref="MeshNode.Order"/> (null last), ties
    /// broken by path. Starts with an empty list so the stage renders before the
    /// first query emission.
    /// </summary>
    private static IObservable<IReadOnlyList<MeshNode>> ObserveDeckSlides(LayoutAreaHost host)
    {
        var parentPath = GetParentPath(host.Hub.Address.ToString());
        var meshService = host.Hub.ServiceProvider.GetService<IMeshService>();
        if (meshService is null || parentPath is null)
            return Observable.Return<IReadOnlyList<MeshNode>>([]);

        return meshService
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"namespace:{parentPath} nodeType:{SlideNodeType.NodeType}"))
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
            .Select(map => (IReadOnlyList<MeshNode>)map.Values
                .OrderBy(n => n.Order ?? int.MaxValue)
                .ThenBy(n => n.Path, StringComparer.Ordinal)
                .ToImmutableList())
            .StartWith((IReadOnlyList<MeshNode>)ImmutableList<MeshNode>.Empty);
    }

    /// <summary>
    /// Finds this slide among the ordered deck slides and returns its index plus the
    /// previous / next sibling (null at either end or while the deck is still loading).
    /// </summary>
    private static (int Index, MeshNode? Prev, MeshNode? Next) Locate(
        string hubPath, IReadOnlyList<MeshNode> slides)
    {
        var index = -1;
        for (var i = 0; i < slides.Count; i++)
            if (string.Equals(slides[i].Path, hubPath, StringComparison.Ordinal))
            {
                index = i;
                break;
            }

        if (index < 0)
            return (-1, null, null);

        return (index,
            index > 0 ? slides[index - 1] : null,
            index < slides.Count - 1 ? slides[index + 1] : null);
    }

    private static string? GetParentPath(string path)
    {
        var idx = path.LastIndexOf('/');
        return idx > 0 ? path[..idx] : null;
    }
}
